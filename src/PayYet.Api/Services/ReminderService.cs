using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;

namespace PayYet.Services;

/// <summary>
/// The daily job (Bangkok time, from <c>Jobs:ReminderHour</c>): creates due recurring bills, then sends reminders.
/// Called by an external scheduler every hour; every bill / recurring item is "claimed" atomically, so repeated or late
/// runs never send or create twice and a missed hour is caught up.
/// </summary>
public class ReminderService(AppDbContext db, LineClient line, BillCardBuilder cards, BillCreationService creation, DraftService drafts,
    IOptions<AppSettings> options, ILogger<ReminderService> log)
{
    public record Result(int GroupMessages, int PrivateMessages, string? Skipped, int RecurringCreated = 0);

    /// <summary>Reminder spacing offered in the UI: never, daily, every 3 days, weekly.</summary>
    public static readonly int[] AllowedEveryDays = { 0, 1, 3, 7 };

    public async Task<Result> RunAsync()
    {
        var now = BangkokTime.Now;
        if (now.Hour < options.Value.Jobs.ReminderHour) return new Result(0, 0, "before reminder hour");

        var today = BangkokTime.Today;
        var created = await RunRecurringAsync(today);

        var todayStart = today.ToDateTime(TimeOnly.MinValue);
        var open = await db.Bills.Include(b => b.Group)
            .Where(b => b.Status == BillStatus.OPEN && b.RemindEveryDays > 0 && (b.LastRemindedOn == null || b.LastRemindedOn < today))
            .ToListAsync();
        // due again once RemindEveryDays have passed since the last reminder (or since the day the bill was created)
        var bills = open.Where(b => today.DayNumber - (b.LastRemindedOn ?? DateOnly.FromDateTime(BangkokTime.FromUtc(b.CreatedAt))).DayNumber >= b.RemindEveryDays).ToList();

        int groupMsgs = 0, privateMsgs = 0;
        foreach (var bill in bills)
        {
            try
            {
                if (BangkokTime.FromUtc(bill.CreatedAt) >= todayStart) continue;   // first reminder is after the creation day

                var items = await db.BillItems.Include(i => i.Debtor).Where(i => i.BillId == bill.Id).ToListAsync();
                var unpaid = items.Where(i => !i.IsSettled && i.Status != ItemStatus.SUBMITTED).ToList();
                if (unpaid.Count == 0) continue;

                var claimed = await db.Bills
                    .Where(b => b.Id == bill.Id && (b.LastRemindedOn == null || b.LastRemindedOn < today))
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.LastRemindedOn, (DateOnly?)today));
                if (claimed == 0) continue;

                if (bill.GroupId.HasValue)
                {
                    if (bill.Group is not { Active: true }) continue;
                    if (await line.PushAsync(bill.Group.LineGroupId, await cards.BuildForGroupAsync(bill, "📌 ยังไม่จ่าย"))) groupMsgs++;
                }
                else
                {
                    // individual bills: each debtor gets a private reminder, but only those who are friends of the bot
                    // (the bot cannot message anyone else; the collector chases them)
                    foreach (var debtor in unpaid.Select(i => i.Debtor).Where(d => d is { IsFriend: true }).Distinct())
                        if (await line.PushAsync(debtor!.LineUserId, await cards.BuildAsync(bill, "📌 ยังไม่จ่าย", debtor.Id))) privateMsgs++;
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Reminder failed for bill {Bill}", bill.Id);
            }
        }
        return new Result(groupMsgs, privateMsgs, null, created);
    }

    // ───────── recurring bills ─────────

    /// <summary>The next run after <paramref name="from"/>: +7 days, or the same day next month (clamped to the month length).</summary>
    public static DateOnly NextRun(string frequency, DateOnly from, int anchorDay)
    {
        if (frequency == "WEEKLY") return from.AddDays(7);
        var first = new DateOnly(from.Year, from.Month, 1).AddMonths(1);
        return new DateOnly(first.Year, first.Month, Math.Min(Math.Max(anchorDay, 1), DateTime.DaysInMonth(first.Year, first.Month)));
    }

    private async Task<int> RunRecurringAsync(DateOnly today)
    {
        var due = await db.RecurringBills.Include(r => r.Creator).Include(r => r.Group)
            .Where(r => r.Active && r.NextRunOn <= today).ToListAsync();
        var made = 0;
        foreach (var rec in due)
        {
            try
            {
                // skip periods that were missed while the job was down: one bill per run, then jump past today
                var next = NextRun(rec.Frequency, rec.NextRunOn, rec.AnchorDay);
                while (next <= today) next = NextRun(rec.Frequency, next, rec.AnchorDay);
                var claimed = await db.RecurringBills.Where(r => r.Id == rec.Id && r.NextRunOn == rec.NextRunOn)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextRunOn, next).SetProperty(r => r.LastRunOn, (DateOnly?)today));
                if (claimed == 0) continue;

                var error = await CreateFromRecurringAsync(rec);
                await db.RecurringBills.Where(r => r.Id == rec.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastError, error));
                if (error == null) made++;
                else await line.PushAsync(rec.Creator.LineUserId, Msg.Text($"⚠️ สร้างบิลประจำ \"{rec.Title}\" ไม่สำเร็จ: {error}"));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Recurring bill {Id} failed", rec.Id);
            }
        }
        return made;
    }

    /// <summary>Builds and announces one bill from a recurring template. Returns an error text, or null on success.</summary>
    private async Task<string?> CreateFromRecurringAsync(RecurringBill rec)
    {
        var user = rec.Creator;
        if (string.IsNullOrEmpty(user.PromptPayId)) return "ผู้เก็บยังไม่ได้ตั้งพร้อมเพย์";

        var ids = JsonSerializer.Deserialize<List<long>>(rec.UserIdsJson) ?? new List<long>();
        Dictionary<long, decimal>? amounts = null;
        if (!string.IsNullOrEmpty(rec.AmountsJson))
            amounts = JsonSerializer.Deserialize<Dictionary<long, long>>(rec.AmountsJson)?.ToDictionary(kv => kv.Key, kv => kv.Value / 100m);

        var proto = new Draft { Title = rec.Title, PerPerson = rec.PerPerson, AmountSatang = rec.AmountSatang };
        var dest = rec.Dest == "G" && rec.GroupId.HasValue ? $"G:{rec.GroupId}" : "P";
        var result = await drafts.BuildFromSelectionAsync(user, proto, ids, dest, amounts);
        if (result.Error != null || result.Draft == null) return result.Error ?? "สร้างบิลไม่สำเร็จ";

        var made = await creation.CreateAsync(user, result.Draft);
        await line.PushAsync(user.LineUserId, Cards.Created(options.Value, "สร้างบิลประจำแล้ว", $"{rec.Title}\n{made.Summary}",
            made.Undo.Select(u => (u.Label, $"act=undo&c={u.ChargeId}"))));
        return null;
    }
}
