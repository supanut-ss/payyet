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
public class PayController(AppDbContext db, LineClient line, PaymentService payments, BillCreationService creation, IOptions<AppSettings> options) : ControllerBase
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

    public record PickRequest(List<long>? UserIds, string? Dest, Dictionary<long, decimal>? Amounts);

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
            groupId = proto.GroupId,
            groups = cand.Groups
        });
    }

    /// <summary>
    /// The collector's choice from the picker page. The page is the confirmation, so the bill is created right away;
    /// the result (with undo buttons) is also pushed to the collector's private chat.
    /// </summary>
    [HttpPost("pick")]
    public async Task<IActionResult> Pick([FromBody] PickRequest req, [FromServices] DraftService drafts)
    {
        var (user, proto, error) = await LoadPickAsync();
        if (error != null) return error;
        if (string.IsNullOrEmpty(user!.PromptPayId))
            return BadRequest(new { error = "ตั้งเลขพร้อมเพย์สำหรับรับเงินก่อน" });

        var result = await drafts.BuildFromSelectionAsync(user, proto!, req.UserIds ?? new List<long>(), req.Dest, req.Amounts);
        if (result.Error != null) return BadRequest(new { error = result.Error });

        var state = await db.States.FindAsync(user.Id);
        if (state != null) { db.States.Remove(state); await db.SaveChangesAsync(); }   // clear first: a double tap cannot create it twice

        var created = await creation.CreateAsync(user, result.Draft!);
        await line.PushAsync(user.LineUserId, Cards.Created(options.Value, "สร้างบิลแล้ว", created.Summary,
            created.Undo.Select(u => (u.Label, $"act=undo&c={u.ChargeId}"))));
        return Ok(new
        {
            summary = created.Summary,
            undo = created.Undo.Select(u => new { label = u.Label, chargeId = u.ChargeId })
        });
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

/// <summary>Collector-only endpoints behind the "manage bills" LIFF page. Identity always comes from a verified LINE ID token.</summary>
[ApiController]
[Route("api/manage")]
public class ManageController(AppDbContext db, LineClient line, BillManageService manage, BillCardBuilder cards, PaymentService payments, IOptions<AppSettings> options) : ControllerBase
{
    public record PaidRequest(long ItemId, decimal? Amount);
    public record IdRequest(long Id);
    public record EditChargeRequest(long Id, string? Title, Dictionary<long, decimal>? Amounts);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" });

        var bills = await manage.OpenBillsAsync(user.Id);
        return Ok(bills.Select(b => new
        {
            id = b.Id,
            groupName = b.Group?.Name,
            title = b.Charges.OrderBy(c => c.Id).Select(c => c.Title).FirstOrDefault() ?? b.Title,
            createdAt = b.CreatedAt,
            totalSatang = b.Items.Sum(i => i.AmountSatang),
            paidSatang = b.Items.Sum(i => i.PaidSatang),
            charges = b.Charges.OrderBy(c => c.Id).Select(c => new
            {
                id = c.Id, title = c.Title, totalSatang = c.TotalSatang,
                shares = c.Shares.Select(s => new { itemId = s.BillItemId, amountSatang = s.AmountSatang })
            }),
            items = b.Items.OrderBy(i => i.Id).Select(i => new
            {
                id = i.Id,
                name = i.Debtor?.Name ?? "?",
                pictureUrl = string.IsNullOrEmpty(i.Debtor?.PictureUrl) ? null : i.Debtor!.PictureUrl,
                amountSatang = i.AmountSatang,
                paidSatang = i.PaidSatang,
                remainingSatang = i.Remaining,
                status = i.Status.ToString(),
                slip = i.Payments.Where(p => p.Status == PaymentStatus.SUBMITTED).OrderByDescending(p => p.Id).Select(p => new
                {
                    id = p.Id,
                    amountSatang = p.AmountSatang,
                    imageUrl = $"{options.Value.App.PublicBaseUrl.TrimEnd('/')}/media/{p.MediaToken}",
                    bankVerified = p.VerifiedBy == "BANK"
                }).FirstOrDefault()
            })
        }));
    }

    [HttpPost("paid")]
    public async Task<IActionResult> Paid([FromBody] PaidRequest req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" });

        var r = await manage.MarkPaidAsync(req.ItemId, user.Id, req.Amount.HasValue ? Money.ToSatang(req.Amount.Value) : null);
        if (!r.Ok) return BadRequest(new { error = r.Message });

        var item = r.Item!;
        var name = item.Debtor?.Name ?? "?";
        var done = r.Bill!.Status == BillStatus.CLOSED;
        var text = item.Remaining == 0
            ? $"✅ {name} จ่ายแล้ว (รับเงินกับผู้เก็บโดยตรง) · บิลของ {user.Name}"
            : $"💵 {name} จ่ายบางส่วนแล้ว คงเหลือ ฿{Money.Format(item.Remaining)} · บิลของ {user.Name}";
        if (done) text += "\n🎉 ทุกคนจ่ายครบแล้ว บิลปิดเรียบร้อย";
        // A payment is not announced in the group: the card is rebuilt from the database, so the next reminder shows who has paid.
        // Only the final "everyone paid" notice goes out right away.
        if (!r.Bill.GroupId.HasValue || done) await Announce(r.Bill, text, item);
        return Ok(new { message = r.Message });
    }

    /// <summary>Rename a charge and/or change what each person owes for it.</summary>
    [HttpPost("edit-charge")]
    public async Task<IActionResult> EditCharge([FromBody] EditChargeRequest req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" });
        var shares = req.Amounts?.ToDictionary(kv => kv.Key, kv => Money.ToSatang(kv.Value));
        var r = await manage.EditChargeAsync(req.Id, user.Id, req.Title, shares);
        return r.Ok ? Ok(new { message = r.Message }) : BadRequest(new { error = r.Message });
    }

    /// <summary>Approve or reject a waiting slip from the manage page (same action as the buttons on the slip card).</summary>
    [HttpPost("approve-slip")]
    public Task<IActionResult> ApproveSlip([FromBody] IdRequest req) => ReviewSlip(req.Id, approve: true);

    [HttpPost("reject-slip")]
    public Task<IActionResult> RejectSlip([FromBody] IdRequest req) => ReviewSlip(req.Id, approve: false);

    private async Task<IActionResult> ReviewSlip(long paymentId, bool approve)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" });
        var message = approve ? await payments.ApproveAsync(paymentId, user.LineUserId) : await payments.RejectAsync(paymentId, user.LineUserId);
        var ok = message.StartsWith("ยืนยันแล้ว", StringComparison.Ordinal) || message.StartsWith("ปฏิเสธแล้ว", StringComparison.Ordinal);
        return ok ? Ok(new { message }) : BadRequest(new { error = message });
    }

    [HttpPost("waive")]
    public async Task<IActionResult> Waive([FromBody] IdRequest req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" });

        var r = await manage.WaiveAsync(req.Id, user.Id);
        if (!r.Ok) return BadRequest(new { error = r.Message });

        var text = $"ผู้เก็บ ({user.Name}) ยกหนี้ให้ {r.Item!.Debtor?.Name} แล้ว";
        if (r.Bill!.Status == BillStatus.CLOSED) text += "\n🎉 บิลปิดเรียบร้อย";
        await Announce(r.Bill, text, r.Item);
        return Ok(new { message = r.Message });
    }

    [HttpPost("cancel-charge")]
    public async Task<IActionResult> CancelCharge([FromBody] IdRequest req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" });

        var r = await manage.CancelChargeAsync(req.Id, user.Id);
        if (!r.Ok) return BadRequest(new { error = r.Message });

        var bill = r.Bill!;
        if (bill.Status != BillStatus.OPEN) await Announce(bill, $"🚫 {user.Name} ยกเลิกบิลแล้ว", null, everyone: true);
        else if (bill.GroupId.HasValue)
        {
            var group = await db.Groups.FindAsync(bill.GroupId.Value);
            if (group is { Active: true }) await line.PushAsync(group.LineGroupId, await cards.BuildForGroupAsync(bill, "↩️ ยกเลิกรายการ"));
        }
        else
        {
            foreach (var debtor in await FriendsOf(bill))
                await line.PushAsync(debtor.LineUserId, await cards.BuildAsync(bill, "↩️ ยกเลิกรายการ", debtor.Id));
        }
        return Ok(new { message = r.Message });
    }

    [HttpPost("cancel-bill")]
    public async Task<IActionResult> CancelBill([FromBody] IdRequest req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" });

        var r = await manage.CancelBillAsync(req.Id, user.Id);
        if (!r.Ok) return BadRequest(new { error = r.Message });

        await Announce(r.Bill!, $"🚫 {user.Name} ยกเลิกบิล \"{r.Bill!.Title}\" แล้ว ไม่ต้องจ่ายบิลนี้", null, everyone: true);
        return Ok(new { message = r.Message });
    }

    // ---- helpers ----

    /// <summary>Group bills: one message in the group. Private bills: the affected debtor (or every debtor) if they are friends of the bot.</summary>
    private async Task Announce(Bill bill, string text, BillItem? item, bool everyone = false)
    {
        if (bill.GroupId.HasValue)
        {
            var group = await db.Groups.FindAsync(bill.GroupId.Value);
            if (group is { Active: true }) await line.PushAsync(group.LineGroupId, Msg.Text(text));
            return;
        }

        var targets = everyone ? await FriendsOf(bill)
            : item?.Debtor is { IsFriend: true } d ? new List<AppUser> { d } : new List<AppUser>();
        foreach (var debtor in targets) await line.PushAsync(debtor.LineUserId, Msg.Text(text));
    }

    private Task<List<AppUser>> FriendsOf(Bill bill) => db.BillItems
        .Where(i => i.BillId == bill.Id && i.Debtor != null && i.Debtor.IsFriend)
        .Select(i => i.Debtor!).Distinct().ToListAsync();

    private async Task<AppUser?> AuthUser()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var who = await line.VerifyIdTokenAsync(header["Bearer ".Length..].Trim());
        return who == null ? null : await db.Users.FirstOrDefaultAsync(u => u.LineUserId == who.Value.UserId);
    }
}
