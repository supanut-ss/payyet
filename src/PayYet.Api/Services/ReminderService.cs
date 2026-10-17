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
        var open = await db.Bills.Include(b => b.Group).Include(b => b.Creator)
            .Where(b => b.Status == BillStatus.OPEN && b.RemindEveryDays > 0).ToListAsync();
        var openIds = open.Select(b => b.Id).ToList();
        var items = await db.BillItems.Include(i => i.Debtor)
            .Where(i => openIds.Contains(i.BillId)).ToListAsync();
        var itemsByBill = items.GroupBy(i => i.BillId).ToDictionary(g => g.Key, g => g.ToList());

        // A bill is due once RemindEveryDays have passed since its last reminder (or since the day it was created),
        // and never on the day it was created. Unpaid = not settled and not waiting for the collector to check a slip.
        bool Unpaid(BillItem i) => !i.IsSettled && i.Status != ItemStatus.SUBMITTED;
        bool Due(Bill b) =>
            BangkokTime.FromUtc(b.CreatedAt) < todayStart
            && (b.LastRemindedOn == null || b.LastRemindedOn < today)
            && today.DayNumber - (b.LastRemindedOn ?? DateOnly.FromDateTime(BangkokTime.FromUtc(b.CreatedAt))).DayNumber >= b.RemindEveryDays
            && itemsByBill.GetValueOrDefault(b.Id)?.Any(Unpaid) == true;

        int groupMsgs = 0, privateMsgs = 0;

        // Reminders are grouped: ONE card per group listing every unpaid bill in it, ONE message per person for their private bills.
        foreach (var g in open.Where(b => b.GroupId.HasValue && Due(b)).GroupBy(b => b.GroupId!.Value))
        {
            try
            {
                var group = g.First().Group;
                if (group is not { Active: true }) continue;
                var all = open.Where(b => b.GroupId == g.Key && itemsByBill.GetValueOrDefault(b.Id)?.Any(Unpaid) == true).ToList();
                if (!await ClaimAsync(all, today)) continue;

                var billLines = all.OrderBy(b => b.Id).Select(b =>
                {
                    var unpaid = itemsByBill[b.Id].Where(Unpaid).ToList();
                    return new Cards.OwedBill(BillTitle(b), "เก็บโดย " + b.Creator.Name, unpaid.Count, itemsByBill[b.Id].Count(i => i.Status != ItemStatus.WAIVED), unpaid.Sum(i => i.Remaining), cards.DetailUrl(b.Id));
                }).ToList();
                var people = all.SelectMany(b => itemsByBill[b.Id].Where(Unpaid)).Where(i => i.Debtor != null)
                    .GroupBy(i => i.Debtor!.Id)
                    .Select(x => new { User = x.First().Debtor!, Count = x.Count(), Sum = x.Sum(i => i.Remaining) })
                    .OrderByDescending(x => x.Sum).ToList();

                var messages = new List<object> { Cards.Outstanding(options.Value, group.Name ?? "กลุ่ม", billLines,
                    people.Select(p => new Cards.OwingPerson(p.User.Name, p.Count, p.Sum)).ToList(), cards.OweUrl()) };
                if (Msg.Mention("📌 ยังไม่จ่าย", people.Select(p => p.User.LineUserId)) is { } tag) messages.Add(tag);
                if (await line.PushAsync(group.LineGroupId, messages.ToArray())) groupMsgs++;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Group reminder failed for group {Group}", g.Key);
            }
        }

        // private bills: one message per debtor (only friends of the bot can be messaged; the collector chases the rest)
        var privateOpen = open.Where(b => !b.GroupId.HasValue).ToList();
        var dueDebtors = privateOpen.Where(Due).SelectMany(b => itemsByBill[b.Id].Where(Unpaid)).Select(i => i.Debtor)
            .Where(d => d is { IsFriend: true }).GroupBy(d => d!.Id).Select(x => x.First()!).ToList();
        foreach (var debtor in dueDebtors)
        {
            try
            {
                var mine = privateOpen.Where(b => itemsByBill[b.Id].Any(i => i.DebtorId == debtor.Id && Unpaid(i))).OrderBy(b => b.Id).ToList();
                if (!await ClaimAsync(mine, today)) continue;
                var lines = mine.Select(b =>
                {
                    var my = itemsByBill[b.Id].Where(i => i.DebtorId == debtor.Id && Unpaid(i)).ToList();
                    return new Cards.OwedBill(BillTitle(b), "เก็บโดย " + b.Creator.Name, 0, 0, my.Sum(i => i.Remaining), cards.DetailUrl(b.Id));
                }).ToList();
                if (await line.PushAsync(debtor.LineUserId, Cards.Outstanding(options.Value, "ของคุณ", lines, Array.Empty<Cards.OwingPerson>(), cards.OweUrl()))) privateMsgs++;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Private reminder failed for debtor {Debtor}", debtor.Id);
            }
        }
        return new Result(groupMsgs, privateMsgs, null, created);
    }

    private static string BillTitle(Bill b) => b.Title;

    /// <summary>Marks the bills as reminded today. False when another run already did (so nothing is sent twice).</summary>
    private async Task<bool> ClaimAsync(List<Bill> bills, DateOnly today)
    {
        var ids = bills.Select(b => b.Id).ToList();
        var claimed = await db.Bills
            .Where(b => ids.Contains(b.Id) && (b.LastRemindedOn == null || b.LastRemindedOn < today))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.LastRemindedOn, (DateOnly?)today));
        return claimed > 0;
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
