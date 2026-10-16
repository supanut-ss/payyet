using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;

namespace PayYet.Services;

/// <summary>
/// Daily 10:00 (Bangkok) reminders. Called by a Plesk scheduled task every hour; each bill is "claimed"
/// atomically per day, so repeated or late runs never send twice and a missed hour is caught up.
/// </summary>
public class ReminderService(AppDbContext db, LineClient line, BillCardBuilder cards, IOptions<AppSettings> options, ILogger<ReminderService> log)
{
    public record Result(int GroupMessages, int PrivateMessages, string? Skipped);

    public async Task<Result> RunAsync()
    {
        var now = BangkokTime.Now;
        if (now.Hour < options.Value.Jobs.ReminderHour) return new Result(0, 0, "before reminder hour");

        var today = BangkokTime.Today;
        var todayStart = today.ToDateTime(TimeOnly.MinValue);
        var bills = await db.Bills.Include(b => b.Group)
            .Where(b => b.Status == BillStatus.OPEN && (b.LastRemindedOn == null || b.LastRemindedOn < today))
            .ToListAsync();

        int groupMsgs = 0, privateMsgs = 0;
        foreach (var bill in bills)
        {
            try
            {
                if (BangkokTime.FromUtc(bill.CreatedAt) >= todayStart) continue;   // first reminder is the day after creation

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
                    if (await line.PushAsync(bill.Group.LineGroupId, await cards.BuildAsync(bill, "📌 ยังไม่จ่าย"))) groupMsgs++;
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
        return new Result(groupMsgs, privateMsgs, null);
    }
}
