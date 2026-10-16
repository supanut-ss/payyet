using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Handlers;
using PayYet.Line;
using PayYet.Services;

namespace PayYet.Controllers;

[ApiController]
public class LineWebhookController(IOptions<AppSettings> options, WebhookProcessor processor, AppDbContext db, ILogger<LineWebhookController> log) : ControllerBase
{
    [HttpPost("api/line/webhook")]
    public async Task<IActionResult> Post()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        if (!LineSignature.IsValid(body, Request.Headers["X-Line-Signature"], options.Value.Line.ChannelSecret))
            return Unauthorized();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return BadRequest(); }

        using (doc)
        {
            if (doc.RootElement.TryGetProperty("events", out var events))
            {
                foreach (var ev in events.EnumerateArray())
                {
                    try { await processor.HandleAsync(ev); }
                    catch (Exception ex)
                    {
                        db.ChangeTracker.Clear();
                        log.LogError(ex, "Webhook event failed");   // never fail the whole request: LINE would redeliver it
                    }
                }
            }
        }
        return Ok();
    }
}

[ApiController]
public class JobsController(IOptions<AppSettings> options, ReminderService reminders, AppDbContext db) : ControllerBase
{
    // Plesk "Fetch a URL" tasks can only send GET, so the key may come as ?key= as well as the X-Job-Key header.
    [HttpGet("jobs/reminders")]
    [HttpPost("jobs/reminders")]
    public async Task<IActionResult> Reminders([FromQuery] string? key)
    {
        var expected = options.Value.Jobs.Key;
        if (string.IsNullOrEmpty(expected)) return StatusCode(503, "Jobs:Key is not configured");
        var given = Request.Headers["X-Job-Key"].FirstOrDefault() ?? key ?? "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
            return Unauthorized();

        var result = await reminders.RunAsync();

        // housekeeping: webhook idempotency rows older than 7 days
        var cutoff = DateTime.UtcNow.AddDays(-7);
        await db.ProcessedEvents.Where(e => e.ProcessedAt < cutoff).ExecuteDeleteAsync();
        return Ok(result);
    }
}

[ApiController]
public class MediaController(AppDbContext db, PaymentService payments) : ControllerBase
{
    /// <summary>Slip image for the collector's LINE push. Unguessable token, valid for 7 days.</summary>
    [HttpGet("media/{token}")]
    public async Task<IActionResult> Get(string token)
    {
        var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.MediaToken == token);
        if (p == null || DateTime.UtcNow - p.CreatedAt > TimeSpan.FromDays(7)) return NotFound();

        var path = Path.Combine(payments.SlipRoot, p.SlipPath);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, path.EndsWith(".png") ? "image/png" : "image/jpeg");
    }
}

/// <summary>Endpoints used by the LIFF pay page. Identity always comes from a verified LINE ID token.</summary>
[ApiController]
[Route("api/pay")]
public class PayController(AppDbContext db, LineClient line, PaymentService payments, IOptions<AppSettings> options) : ControllerBase
{
    [HttpGet("config")]
    public IActionResult Config() => Ok(new { liffId = options.Value.Liff.Id, oaUrl = options.Value.App.LineOaUrl });

    [HttpGet("{billToken}")]
    public async Task<IActionResult> Get(string billToken)
    {
        var who = await Authenticate();
        if (who == null) return Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" });

        var item = await db.BillItems
            .Include(i => i.Bill).ThenInclude(b => b.Creator)
            .Include(i => i.Bill).ThenInclude(b => b.Charges)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Bill.Token == billToken && i.Debtor!.LineUserId == who.Value.UserId);
        if (item == null) return StatusCode(403, new { error = "คุณไม่อยู่ในบิลนี้" });

        var bill = item.Bill;
        string? qr = null;
        string? promptPayId = bill.Creator.PromptPayId;
        if (!string.IsNullOrEmpty(promptPayId) && item.Remaining > 0 && bill.Status == BillStatus.OPEN)
        {
            var payload = PromptPay.Payload(promptPayId, item.Remaining);
            using var gen = new QRCoder.QRCodeGenerator();
            var data = gen.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
            qr = "data:image/png;base64," + Convert.ToBase64String(new QRCoder.PngByteQRCode(data).GetGraphic(8));
        }

        return Ok(new
        {
            title = bill.Title,
            collector = bill.Creator.Name,
            billStatus = bill.Status.ToString(),
            charges = bill.Charges.OrderBy(c => c.Id).Select(c => new { c.Title, total = c.TotalSatang }),
            amountSatang = item.AmountSatang,
            paidSatang = item.PaidSatang,
            remainingSatang = item.Remaining,
            itemStatus = item.Status.ToString(),
            promptPayId,
            qr,
            payments = item.Payments.OrderByDescending(p => p.Id).Select(p => new
            {
                amountSatang = p.AmountSatang,
                status = p.Status.ToString(),
                at = p.CreatedAt
            })
        });
    }

    public record PickRequest(List<long>? UserIds, string? Dest);

    /// <summary>People-picker page: who can this collector bill, and what is being billed.</summary>
    [HttpGet("pick")]
    public async Task<IActionResult> PickInfo([FromServices] DraftService drafts, [FromServices] MemberService members)
    {
        var (user, proto, error) = await LoadPickAsync();
        if (error != null) return error;

        await members.BackfillPicturesAsync(user!.Id);   // first open fetches missing avatars once; later opens are instant
        var cand = await drafts.GetCandidatesAsync(user!);
        return Ok(new
        {
            title = proto!.Title,
            amountSatang = proto.AmountSatang,
            perPerson = proto.PerPerson,
            groups = cand.Groups
        });
    }

    /// <summary>The collector's choice: people and where to announce. The confirm card is pushed to their chat.</summary>
    [HttpPost("pick")]
    public async Task<IActionResult> Pick([FromBody] PickRequest req, [FromServices] DraftService drafts, [FromServices] DraftCardBuilder draftCards)
    {
        var (user, proto, error) = await LoadPickAsync();
        if (error != null) return error;

        var result = await drafts.BuildFromSelectionAsync(user!, proto!, req.UserIds ?? new List<long>(), req.Dest);
        if (result.Error != null) return BadRequest(new { error = result.Error });

        var draft = result.Draft!;
        var state = (await db.States.FindAsync(user!.Id))!;
        state.Flow = "CONFIRM";
        state.Step = "confirm";
        state.Data = JsonSerializer.Serialize(draft);
        state.ExpiresAt = DateTime.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync();

        await line.PushAsync(user.LineUserId, await draftCards.BuildAsync(user, draft));
        return Ok(new { message = "ส่งการ์ดยืนยันไปที่แชทแล้ว" });
    }

    private async Task<(AppUser? User, Draft? Proto, IActionResult? Error)> LoadPickAsync()
    {
        var who = await Authenticate();
        if (who == null) return (null, null, Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" }));

        var user = await db.Users.FirstOrDefaultAsync(u => u.LineUserId == who.Value.UserId);
        var state = user == null ? null : await db.States.FindAsync(user.Id);
        if (user == null || state is not { Flow: "PICK" } || state.Data == null || state.ExpiresAt < DateTime.UtcNow)
            return (null, null, BadRequest(new { error = "หมดเวลาแล้ว กลับไปพิมพ์คำสั่งเก็บเงินในแชทกับบอทอีกครั้ง" }));

        return (user, JsonSerializer.Deserialize<Draft>(state.Data), null);
    }

    [HttpPost("slip")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Slip([FromForm] string billToken, [FromForm] decimal amount, IFormFile? file)
    {
        var who = await Authenticate();
        if (who == null) return Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" });
        if (file == null || file.Length == 0) return BadRequest(new { error = "กรุณาแนบรูปสลิป" });
        if (file.Length > PaymentService.MaxSlipBytes) return BadRequest(new { error = "ไฟล์ใหญ่เกิน 5 MB" });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var (ok, message) = await payments.SubmitSlipAsync(who.Value.UserId, billToken, Money.ToSatang(amount), ms.ToArray());
        return ok ? Ok(new { message }) : BadRequest(new { error = message });
    }

    private async Task<(string UserId, string? Name)?> Authenticate()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        return await line.VerifyIdTokenAsync(header["Bearer ".Length..].Trim());
    }
}
