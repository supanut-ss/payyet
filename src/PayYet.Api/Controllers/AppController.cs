using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;
using PayYet.Services;

namespace PayYet.Controllers;

/// <summary>
/// Endpoints behind the LIFF "app" screens (home, new bill form, what I owe, PromptPay setting).
/// Every call needs a LINE ID token that LINE verifies; the user is always taken from the token.
/// </summary>
[ApiController]
[Route("api/app")]
public class AppController(AppDbContext db, LineClient line, DraftService drafts, MemberService members, BillCreationService creation) : ControllerBase
{
    private const decimal MaxAmountBaht = 1_000_000m;

    public record PromptPayRequest(string? Id);
    public record CreateRequest(string? Title, decimal Amount, bool PerPerson, List<long>? UserIds, string? Dest, Dictionary<long, decimal>? Amounts = null);
    public record UndoRequest(long ChargeId);

    [HttpGet("home")]
    public async Task<IActionResult> Home()
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);

        var open = await db.BillItems.Where(i => i.Bill.CreatorId == user.Id && i.Bill.Status == BillStatus.OPEN).ToListAsync();
        var receivable = open.Where(i => !i.IsSettled).Sum(i => i.Remaining);
        var openBills = open.Select(i => i.BillId).Distinct().Count();

        var owe = await db.BillItems.Where(i => i.DebtorId == user.Id && i.Bill.Status == BillStatus.OPEN).ToListAsync();
        var oweOpen = owe.Where(i => !i.IsSettled).ToList();

        return Ok(new
        {
            name = user.Name,
            pictureUrl = string.IsNullOrEmpty(user.PictureUrl) ? null : user.PictureUrl,
            hasPromptPay = !string.IsNullOrEmpty(user.PromptPayId),
            promptPayMasked = Mask(user.PromptPayId),
            openBills,
            receivableSatang = receivable,
            oweCount = oweOpen.Count,
            oweSatang = oweOpen.Sum(i => i.Remaining)
        });
    }

    [HttpPost("promptpay")]
    public async Task<IActionResult> SetPromptPay([FromBody] PromptPayRequest req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);
        if (string.IsNullOrWhiteSpace(req.Id) || !PromptPay.IsValidId(req.Id))
            return BadRequest(new { error = "ใช้เบอร์มือถือ 10 หลัก หรือเลขบัตรประชาชน 13 หลัก" });

        // The collector must have added the bot as a friend: slips and receipts are sent to them privately.
        if (!user.IsFriend)
        {
            if (await line.GetProfileAsync(user.LineUserId) == null)
                return BadRequest(new { error = "ต้องเพิ่มบอทเป็นเพื่อนก่อน จึงจะรับสลิปและแจ้งเตือนได้", needFriend = true, oaUrl = await line.GetOaUrlAsync() });
            user.IsFriend = true;
        }

        user.PromptPayId = new string(req.Id.Where(char.IsDigit).ToArray());
        await db.SaveChangesAsync();
        return Ok(new { message = "บันทึกพร้อมเพย์แล้ว", promptPayMasked = Mask(user.PromptPayId) });
    }

    /// <summary>Everything the "new bill" form needs: the groups the collector can bill, with members and pictures.</summary>
    [HttpGet("new")]
    public async Task<IActionResult> NewBillData()
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);

        await members.BackfillPicturesAsync(user.Id);   // fetches missing avatars once
        var cand = await drafts.GetCandidatesAsync(user);
        return Ok(new { hasPromptPay = !string.IsNullOrEmpty(user.PromptPayId), groups = cand.Groups });
    }

    /// <summary>Creates the bill straight from the form (the form is the confirmation step).</summary>
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreateRequest req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);
        if (string.IsNullOrEmpty(user.PromptPayId))
            return BadRequest(new { error = "ตั้งเลขพร้อมเพย์สำหรับรับเงินก่อน" });
        if (req.Amount <= 0 || req.Amount > MaxAmountBaht)
            return BadRequest(new { error = $"ใส่ยอดเงินให้ถูกต้อง (มากกว่า 0 และไม่เกิน {MaxAmountBaht:N0} บาท)" });

        var title = string.IsNullOrWhiteSpace(req.Title) ? $"ยอดเรียกเก็บ {BangkokTime.Today:d/M}" : req.Title.Trim();
        if (title.Length > 80) title = title[..80];

        var proto = new Draft { Title = title, PerPerson = req.PerPerson, AmountSatang = Money.ToSatang(req.Amount) };
        var result = await drafts.BuildFromSelectionAsync(user, proto, req.UserIds ?? new List<long>(), req.Dest, req.Amounts);
        if (result.Error != null) return BadRequest(new { error = result.Error });

        var created = await creation.CreateAsync(user, result.Draft!);
        return Ok(new
        {
            summary = created.Summary,
            undo = created.Undo.Select(u => new { label = u.Label, chargeId = u.ChargeId })
        });
    }

    [HttpPost("undo")]
    public async Task<IActionResult> Undo([FromBody] UndoRequest req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);

        var (ok, message) = await creation.UndoAsync(user, req.ChargeId);
        return ok ? Ok(new { message }) : BadRequest(new { error = message });
    }

    /// <summary>
    /// Paid history for a date range (Bangkok days, inclusive; default the last 30 days).
    /// role "owe" = what I paid to others, "collected" = what others paid me.
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] string? role, [FromQuery] string? from, [FromQuery] string? to)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);

        var today = BangkokTime.Today;
        var toDay = DateOnly.TryParse(to, out var t) ? t : today;
        var fromDay = DateOnly.TryParse(from, out var f) ? f : toDay.AddDays(-30);
        if (fromDay > toDay) (fromDay, toDay) = (toDay, fromDay);
        if (toDay.DayNumber - fromDay.DayNumber > 366) fromDay = toDay.AddDays(-366);

        var startUtc = BangkokTime.StartOfDayUtc(fromDay);
        var endUtc = BangkokTime.StartOfDayUtc(toDay.AddDays(1));
        var collected = role == "collected";

        var q = db.BillItems
            .Include(i => i.Bill).ThenInclude(b => b.Creator)
            .Include(i => i.Bill).ThenInclude(b => b.Group)
            .Include(i => i.Bill).ThenInclude(b => b.Charges)
            .Include(i => i.Debtor)
            .Where(i => i.Status == ItemStatus.PAID && i.PaidAt != null && i.PaidAt >= startUtc && i.PaidAt < endUtc);
        q = collected ? q.Where(i => i.Bill.CreatorId == user.Id) : q.Where(i => i.DebtorId == user.Id);

        var rows = await q.OrderByDescending(i => i.PaidAt).Take(500).ToListAsync();
        return Ok(new
        {
            role = collected ? "collected" : "owe",
            from = fromDay.ToString("yyyy-MM-dd"),
            to = toDay.ToString("yyyy-MM-dd"),
            count = rows.Count,
            totalSatang = rows.Sum(i => i.PaidSatang),
            items = rows.Select(i =>
            {
                var other = collected ? i.Debtor : i.Bill.Creator;
                return new
                {
                    title = i.Bill.Charges.OrderBy(c => c.Id).Select(c => c.Title).FirstOrDefault() ?? i.Bill.Title,
                    name = other?.Name ?? "?",
                    pictureUrl = string.IsNullOrEmpty(other?.PictureUrl) ? null : other!.PictureUrl,
                    groupName = i.Bill.Group?.Name,
                    amountSatang = i.PaidSatang,
                    paidAt = BangkokTime.FromUtc(i.PaidAt!.Value).ToString("yyyy-MM-dd HH:mm")
                };
            })
        });
    }

    /// <summary>What the signed-in user still has to pay, with the token that opens the pay page.</summary>
    [HttpGet("owe")]
    public async Task<IActionResult> Owe()
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);

        var items = await db.BillItems
            .Include(i => i.Bill).ThenInclude(b => b.Creator)
            .Include(i => i.Bill).ThenInclude(b => b.Charges)
            .Where(i => i.DebtorId == user.Id && i.Bill.Status == BillStatus.OPEN)
            .OrderByDescending(i => i.Id).Take(50).ToListAsync();

        return Ok(items.Where(i => !i.IsSettled).Select(i => new
        {
            billToken = i.Bill.Token,
            title = i.Bill.Charges.OrderBy(c => c.Id).Select(c => c.Title).FirstOrDefault() ?? i.Bill.Title,
            collector = i.Bill.Creator.Name,
            collectorPictureUrl = string.IsNullOrEmpty(i.Bill.Creator.PictureUrl) ? null : i.Bill.Creator.PictureUrl,
            amountSatang = i.AmountSatang,
            paidSatang = i.PaidSatang,
            remainingSatang = i.Remaining,
            status = i.Status.ToString()
        }));
    }

    // ---- helpers ----

    private static readonly object NotSignedIn = new { error = "เข้าสู่ระบบไม่สำเร็จ ลองเปิดจากแอป LINE อีกครั้ง" };

    /// <summary>"••••••5678": enough to recognise the number without showing it in full.</summary>
    private static string? Mask(string? id) =>
        string.IsNullOrEmpty(id) || id.Length < 5 ? null : new string('•', id.Length - 4) + id[^4..];

    private async Task<AppUser?> AuthUser()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var who = await line.VerifyIdTokenAsync(header["Bearer ".Length..].Trim());
        if (who == null) return null;

        var user = await db.Users.FirstOrDefaultAsync(u => u.LineUserId == who.Value.UserId);
        if (user != null) return user;

        // someone who opens the app before ever messaging the bot: create them from the LINE profile in the ID token
        user = new AppUser { LineUserId = who.Value.UserId, DisplayName = who.Value.Name, IsFriend = false, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            user = await db.Users.FirstAsync(u => u.LineUserId == who.Value.UserId);
        }
        return user;
    }
}
