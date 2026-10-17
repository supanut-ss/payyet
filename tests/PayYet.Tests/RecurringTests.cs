using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Services;

namespace PayYet.Tests;

public class RecurringTests : DbTestBase
{
    private readonly AppUser _me, _a, _b;
    private readonly ChatGroup _group;
    private readonly BillService _bills;
    private readonly ReminderService _job;

    public RecurringTests()
    {
        _me = AddUser("ผู้เก็บ", promptPay: "0812345678");
        _a = AddUser("เอ");
        _b = AddUser("บี");
        _group = AddGroup("กลุ่ม", _me, _a, _b);
        _bills = new BillService(Db);
        var line = NewLine();
        var creation = new BillCreationService(Db, line, _bills, new BillCardBuilder(Db, Options));
        _job = new ReminderService(Db, line, new BillCardBuilder(Db, Options), creation, new DraftService(Db), Options, NullLogger<ReminderService>.Instance);
    }

    [Fact]
    public void Next_run_is_a_week_later_or_the_same_day_next_month_clamped_to_the_month_length()
    {
        Assert.Equal(new DateOnly(2026, 10, 24), ReminderService.NextRun("WEEKLY", new DateOnly(2026, 10, 17), 17));
        Assert.Equal(new DateOnly(2026, 11, 17), ReminderService.NextRun("MONTHLY", new DateOnly(2026, 10, 17), 17));
        Assert.Equal(new DateOnly(2027, 2, 28), ReminderService.NextRun("MONTHLY", new DateOnly(2027, 1, 31), 31));   // no 31st in February
        Assert.Equal(new DateOnly(2027, 1, 31), ReminderService.NextRun("MONTHLY", new DateOnly(2026, 12, 31), 31));
    }

    [Fact]
    public async Task A_due_recurring_bill_is_created_once_and_scheduled_forward()
    {
        var today = BangkokTime.Today;
        Db.RecurringBills.Add(new RecurringBill
        {
            CreatorId = _me.Id, GroupId = _group.Id, Dest = "G", Title = "ค่าเน็ต", AmountSatang = 30000, PerPerson = false,
            UserIdsJson = JsonSerializer.Serialize(new[] { _a.Id, _b.Id }), Frequency = "MONTHLY", AnchorDay = today.Day,
            NextRunOn = today, CreatedAt = DateTime.UtcNow
        });
        await Db.SaveChangesAsync();

        var first = await _job.RunAsync();
        Assert.Equal(1, first.RecurringCreated);
        Assert.Equal(1, await Db.Bills.CountAsync());
        Assert.All(await Db.BillItems.ToListAsync(), i => Assert.Equal(15000, i.AmountSatang));

        Db.ChangeTracker.Clear();
        var rec = await Db.RecurringBills.FirstAsync();
        Assert.True(rec.NextRunOn > today);
        Assert.Equal(today, rec.LastRunOn);
        Assert.Null(rec.LastError);

        Assert.Equal(0, (await _job.RunAsync()).RecurringCreated);     // a second run the same day creates nothing
    }

    [Fact]
    public async Task A_paused_or_unfunded_recurring_bill_is_not_created()
    {
        var today = BangkokTime.Today;
        var noPromptPay = AddUser("ไม่มีพร้อมเพย์");
        AddGroup("อีกกลุ่ม", noPromptPay, _a);
        var ids = JsonSerializer.Serialize(new[] { _a.Id });
        Db.RecurringBills.Add(new RecurringBill { CreatorId = _me.Id, GroupId = _group.Id, Title = "หยุดอยู่", AmountSatang = 10000, UserIdsJson = ids, Frequency = "WEEKLY", NextRunOn = today, Active = false, CreatedAt = DateTime.UtcNow });
        Db.RecurringBills.Add(new RecurringBill { CreatorId = noPromptPay.Id, GroupId = _group.Id, Title = "ไม่มีพร้อมเพย์", AmountSatang = 10000, UserIdsJson = ids, Frequency = "WEEKLY", NextRunOn = today, CreatedAt = DateTime.UtcNow });
        await Db.SaveChangesAsync();

        Assert.Equal(0, (await _job.RunAsync()).RecurringCreated);
        Assert.Equal(0, await Db.Bills.CountAsync());
        Db.ChangeTracker.Clear();
        Assert.Equal("ผู้เก็บยังไม่ได้ตั้งพร้อมเพย์", (await Db.RecurringBills.FirstAsync(r => r.Title == "ไม่มีพร้อมเพย์")).LastError);
    }

    [Fact]
    public async Task Reminders_follow_each_bills_frequency()
    {
        async Task<Bill> OldBill(string title, int everyDays, int createdDaysAgo)
        {
            var r = await _bills.AddChargeAsync(_me.Id, _group.Id, null, title, SplitMode.EQUAL, new[] { (_a.Id, 10000L) });
            var bill = await Db.Bills.FirstAsync(b => b.Id == r.Bill.Id);
            bill.RemindEveryDays = everyDays;
            bill.CreatedAt = DateTime.UtcNow.AddDays(-createdDaysAgo);
            bill.ScopeKey = null;          // lets the next bill in the same group be separate
            await Db.SaveChangesAsync();
            return bill;
        }

        var daily = await OldBill("รายวัน", 1, 2);
        var every3 = await OldBill("ทุก 3 วัน", 3, 2);        // only 2 days old: not due yet
        var off = await OldBill("ปิดเตือน", 0, 5);
        var weekly = await OldBill("รายสัปดาห์", 7, 8);       // 8 days old: due

        var result = await _job.RunAsync();

        // one combined card for the group; it lists every unpaid bill that reminds at all (the "off" bill stays out)
        Assert.Equal(1, result.GroupMessages);
        Db.ChangeTracker.Clear();
        var reminded = await Db.Bills.Where(b => b.LastRemindedOn != null).Select(b => b.Id).ToListAsync();
        Assert.Contains(daily.Id, reminded);
        Assert.Contains(weekly.Id, reminded);
        Assert.Contains(every3.Id, reminded);
        Assert.DoesNotContain(off.Id, reminded);
        var card = System.Text.RegularExpressions.Regex.Unescape(Handler.Calls.Last(c => c.Url.EndsWith("/message/push")).Body);
        Assert.Contains("3 บิล", card);          // "ค้างอยู่ 3 บิล"
    }

    [Fact]
    public async Task One_private_reminder_lists_every_bill_a_person_owes()
    {
        var other = AddUser("ผู้เก็บอีกคน", promptPay: "0899999999");
        for (var n = 0; n < 2; n++)
            await _bills.AddChargeAsync(_me.Id, null, _a.Id, "บิล" + n, SplitMode.EQUAL, new[] { (_a.Id, 10000L) });
        await _bills.AddChargeAsync(other.Id, null, _a.Id, "ของอีกคน", SplitMode.EQUAL, new[] { (_a.Id, 5000L) });
        foreach (var b in await Db.Bills.ToListAsync()) b.CreatedAt = DateTime.UtcNow.AddDays(-2);
        await Db.SaveChangesAsync();

        var result = await _job.RunAsync();

        Assert.Equal(1, result.PrivateMessages);                       // three bills, one message
        Assert.Equal(1, Handler.Pushes(_a.LineUserId));
        var body = System.Text.RegularExpressions.Regex.Unescape(Handler.Calls.Last(c => c.Url.EndsWith("/message/push")).Body);
        Assert.Contains("3 บิล", body);
        Assert.Contains("ของอีกคน", body);
    }
}
