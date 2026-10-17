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
    public record CreateRequest(string? Title, decimal Amount, bool PerPerson, List<long>? UserIds, string? Dest, Dictionary<long, decimal>? Amounts = null, string? Repeat = null);
    public record SettingsRequest(int RemindEveryDays);
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
            remindEveryDays = user.RemindEveryDays,
            recurringCount = await db.RecurringBills.CountAsync(r => r.CreatorId == user.Id && r.Active),
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

        // "ทำซ้ำ": remember this bill as a template; the daily job creates the next one a week / a month later
        var repeatNote = "";
        var freq = req.Repeat?.ToLowerInvariant() switch { "weekly" => "WEEKLY", "monthly" => "MONTHLY", _ => null };
        if (freq != null)
        {
            var d = result.Draft!;
            var today = BangkokTime.Today;
            db.RecurringBills.Add(new RecurringBill
            {
                CreatorId = user.Id,
                GroupId = d.Dest == "G" ? d.GroupId : null,
                Dest = d.Dest,
                Title = d.Title,
                AmountSatang = d.Custom ? d.Total : Money.ToSatang(req.Amount),
                PerPerson = req.PerPerson,
                UserIdsJson = System.Text.Json.JsonSerializer.Serialize(d.Debtors.Select(x => x.UserId)),
                AmountsJson = d.Custom ? System.Text.Json.JsonSerializer.Serialize(d.Debtors.ToDictionary(x => x.UserId, x => x.Satang ?? 0)) : null,
                Frequency = freq,
                AnchorDay = today.Day,
                NextRunOn = ReminderService.NextRun(freq, today, today.Day),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            repeatNote = freq == "WEEKLY" ? "\n🔁 สร้างบิลนี้ซ้ำทุกสัปดาห์" : "\n🔁 สร้างบิลนี้ซ้ำทุกเดือน";
        }

        return Ok(new
        {
            summary = created.Summary + repeatNote,
            undo = created.Undo.Select(u => new { label = u.Label, chargeId = u.ChargeId })
        });
    }

    /// <summary>Default reminder spacing for the bills this user creates (0 = never, 1 = daily, 3, 7).</summary>
    [HttpPost("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] SettingsRequest req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);
        if (!ReminderService.AllowedEveryDays.Contains(req.RemindEveryDays)) return BadRequest(new { error = "ตัวเลือกไม่ถูกต้อง" });
        user.RemindEveryDays = req.RemindEveryDays;
        await db.SaveChangesAsync();
        return Ok(new { message = "บันทึกแล้ว" });
    }

    // ---- recurring bills ----

    [HttpGet("recurring")]
    public async Task<IActionResult> Recurring()
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);
        var rows = await db.RecurringBills.Include(r => r.Group).Where(r => r.CreatorId == user.Id).OrderByDescending(r => r.Id).ToListAsync();
        return Ok(rows.Select(r => new
        {
            id = r.Id,
            title = r.Title,
            groupName = r.Group?.Name,
            frequency = r.Frequency,
            nextRunOn = r.NextRunOn.ToString("yyyy-MM-dd"),
            amountSatang = r.AmountSatang,
            perPerson = r.PerPerson,
            people = System.Text.Json.JsonSerializer.Deserialize<List<long>>(r.UserIdsJson)?.Count ?? 0,
            active = r.Active,
            lastError = r.LastError
        }));
    }

    public record RecurringId(long Id);

    [HttpPost("recurring/toggle")]
    public async Task<IActionResult> ToggleRecurring([FromBody] RecurringId req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);
        var rec = await db.RecurringBills.FirstOrDefaultAsync(r => r.Id == req.Id && r.CreatorId == user.Id);
        if (rec == null) return NotFound(new { error = "ไม่พบบิลประจำนี้" });
        rec.Active = !rec.Active;
        if (rec.Active)   // resuming: never run for a date that already passed
        {
            var today = BangkokTime.Today;
            while (rec.NextRunOn <= today) rec.NextRunOn = ReminderService.NextRun(rec.Frequency, rec.NextRunOn, rec.AnchorDay);
        }
        await db.SaveChangesAsync();
        return Ok(new { message = rec.Active ? "เปิดใช้งานแล้ว" : "หยุดชั่วคราวแล้ว" });
    }

    [HttpPost("recurring/delete")]
    public async Task<IActionResult> DeleteRecurring([FromBody] RecurringId req)
    {
        var user = await AuthUser();
        if (user == null) return Unauthorized(NotSignedIn);
        var rec = await db.RecurringBills.FirstOrDefaultAsync(r => r.Id == req.Id && r.CreatorId == user.Id);
        if (rec == null) return NotFound(new { error = "ไม่พบบิลประจำนี้" });
        db.RecurringBills.Remove(rec);
        await db.SaveChangesAsync();
        return Ok(new { message = "ลบบิลประจำแล้ว" });
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

    /// <summary>Same data as <see cref="History"/> as a CSV file (UTF-8 with BOM so Excel shows Thai correctly).</summary>
    [HttpGet("history/export")]
    public async Task<IActionResult> ExportHistory([FromQuery] string? role, [FromQuery] string? from, [FromQuery] string? to)
    {
        var result = await History(role, from, to);
        if (result is not OkObjectResult { Value: { } v }) return result;

        static string Cell(string? t)
        {
            t ??= "";
            if (t.Length > 0 && "=+-@".Contains(t[0])) t = "'" + t;                  // no spreadsheet formula injection
            return "\"" + t.Replace("\"", "\"\"") + "\"";
        }

        var json = System.Text.Json.JsonSerializer.SerializeToElement(v);
        var collected = json.GetProperty("role").GetString() == "collected";
        var sb = new System.Text.StringBuilder();
        sb.Append("วันที่,รายการ,").Append(collected ? "ผู้จ่าย" : "ผู้เก็บ").Append(",กลุ่ม,จำนวนเงิน (บาท)\r\n");
        foreach (var i in json.GetProperty("items").EnumerateArray())
        {
            sb.Append(Cell(i.GetProperty("paidAt").GetString())).Append(',')
              .Append(Cell(i.GetProperty("title").GetString())).Append(',')
              .Append(Cell(i.GetProperty("name").GetString())).Append(',')
              .Append(Cell(i.TryGetProperty("groupName", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.String ? g.GetString() : null)).Append(',')
              .Append((i.GetProperty("amountSatang").GetInt64() / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        }
        sb.Append(",,,รวม,").Append((json.GetProperty("totalSatang").GetInt64() / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");

        var bytes = new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"payyet-{json.GetProperty("from").GetString()}_{json.GetProperty("to").GetString()}.csv");
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
