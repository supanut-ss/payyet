using Microsoft.EntityFrameworkCore;
using PayYet.Data;
using PayYet.Services;

namespace PayYet.Tests;

public class ManageTests : DbTestBase
{
    private readonly AppUser _me, _a, _b, _stranger;
    private readonly ChatGroup _group;
    private readonly BillService _bills;
    private readonly BillManageService _manage;

    public ManageTests()
    {
        _me = AddUser("ผู้เก็บ");
        _a = AddUser("เอ");
        _b = AddUser("บี");
        _stranger = AddUser("คนอื่น");
        _group = AddGroup("กลุ่ม", _me, _a, _b);
        _bills = new BillService(Db);
        _manage = new BillManageService(Db, _bills);
    }

    private async Task<AddChargeResult> NewBill(params (AppUser User, long Satang)[] shares) =>
        await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ค่าข้าว", SplitMode.EQUAL, shares.Select(s => (s.User.Id, s.Satang)).ToList());

    private Task<BillItem> ItemOf(AddChargeResult r, AppUser u) => Db.BillItems.AsNoTracking().FirstAsync(i => i.BillId == r.Bill.Id && i.DebtorId == u.Id);

    // ───────── cash ─────────

    [Fact]
    public async Task Cash_payment_in_full_marks_the_person_paid_and_closes_the_bill_when_everyone_is_done()
    {
        var r = await NewBill((_a, 10000), (_b, 10000));
        var itemA = await ItemOf(r, _a);

        var res = await _manage.MarkPaidAsync(itemA.Id, _me.Id);
        Assert.True(res.Ok);
        Assert.Equal(ItemStatus.PAID, (await ItemOf(r, _a)).Status);
        Assert.Equal(BillStatus.OPEN, (await Db.Bills.AsNoTracking().FirstAsync(b => b.Id == r.Bill.Id)).Status);
        Assert.Equal(1, await Db.Payments.CountAsync(p => p.BillItemId == itemA.Id && p.VerifiedBy == "CASH" && p.Status == PaymentStatus.APPROVED));

        var itemB = await ItemOf(r, _b);
        await _manage.MarkPaidAsync(itemB.Id, _me.Id);
        Db.ChangeTracker.Clear();
        var bill = await Db.Bills.FirstAsync(b => b.Id == r.Bill.Id);
        Assert.Equal(BillStatus.CLOSED, bill.Status);
        Assert.Null(bill.ScopeKey);
    }

    [Fact]
    public async Task Partial_cash_leaves_the_remainder_and_rejects_overpayment()
    {
        var r = await NewBill((_a, 10000));
        var item = await ItemOf(r, _a);

        Assert.False((await _manage.MarkPaidAsync(item.Id, _me.Id, 10001)).Ok);   // more than owed
        Assert.False((await _manage.MarkPaidAsync(item.Id, _me.Id, 0)).Ok);

        var part = await _manage.MarkPaidAsync(item.Id, _me.Id, 4000);
        Assert.True(part.Ok);
        var after = await ItemOf(r, _a);
        Assert.Equal(4000, after.PaidSatang);
        Assert.Equal(ItemStatus.PENDING, after.Status);
        Assert.Equal(6000, after.Remaining);

        Assert.True((await _manage.MarkPaidAsync(item.Id, _me.Id)).Ok);          // default = the rest
        Assert.Equal(ItemStatus.PAID, (await ItemOf(r, _a)).Status);
        Assert.False((await _manage.MarkPaidAsync(item.Id, _me.Id)).Ok);          // already settled
    }

    [Fact]
    public async Task Only_the_collector_can_manage_and_a_waiting_slip_must_be_decided_first()
    {
        var r = await NewBill((_a, 10000), (_b, 10000));
        var itemA = await ItemOf(r, _a);

        Assert.False((await _manage.MarkPaidAsync(itemA.Id, _a.Id)).Ok);        // the debtor cannot mark himself paid
        Assert.False((await _manage.WaiveAsync(itemA.Id, _stranger.Id)).Ok);
        Assert.False((await _manage.CancelBillAsync(r.Bill.Id, _a.Id)).Ok);

        Db.ChangeTracker.Clear();
        (await Db.BillItems.FirstAsync(i => i.Id == itemA.Id)).Status = ItemStatus.SUBMITTED;
        await Db.SaveChangesAsync();
        Assert.False((await _manage.MarkPaidAsync(itemA.Id, _me.Id)).Ok);
        Assert.False((await _manage.WaiveAsync(itemA.Id, _me.Id)).Ok);
    }

    // ───────── waive ─────────

    [Fact]
    public async Task Waiving_removes_the_debt_from_the_totals_and_can_close_the_bill()
    {
        var r = await NewBill((_a, 10000), (_b, 10000));
        await _manage.MarkPaidAsync((await ItemOf(r, _a)).Id, _me.Id);
        var res = await _manage.WaiveAsync((await ItemOf(r, _b)).Id, _me.Id);

        Assert.True(res.Ok);
        Assert.Equal(ItemStatus.WAIVED, (await ItemOf(r, _b)).Status);
        Db.ChangeTracker.Clear();
        Assert.Equal(BillStatus.CLOSED, (await Db.Bills.FirstAsync(b => b.Id == r.Bill.Id)).Status);
        Assert.False((await _manage.WaiveAsync((await ItemOf(r, _b)).Id, _me.Id)).Ok);   // twice
    }

    // ───────── cancel one charge ─────────

    [Fact]
    public async Task Cancelling_a_charge_lowers_everyones_amount_even_after_the_ten_minute_window()
    {
        var food = await NewBill((_a, 10000), (_b, 10000));
        var drinks = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ค่าเครื่องดื่ม", SplitMode.EQUAL, new[] { (_a.Id, 3000L), (_b.Id, 3000L) });
        drinks.Charge.CreatedAt = DateTime.UtcNow.AddHours(-5);     // the chat "undo" would refuse this
        await Db.SaveChangesAsync();

        var res = await _manage.CancelChargeAsync(drinks.Charge.Id, _me.Id);
        Assert.True(res.Ok);
        Db.ChangeTracker.Clear();
        Assert.All(await Db.BillItems.Where(i => i.BillId == food.Bill.Id).ToListAsync(), i => Assert.Equal(10000, i.AmountSatang));
        Assert.Equal(1, await Db.BillCharges.CountAsync(c => c.BillId == food.Bill.Id));
    }

    [Fact]
    public async Task Cancelling_a_charge_after_part_payment_settles_people_who_already_paid_enough()
    {
        var food = await NewBill((_a, 10000), (_b, 10000));
        await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ค่าเครื่องดื่ม", SplitMode.EQUAL, new[] { (_a.Id, 3000L), (_b.Id, 3000L) });
        await _manage.MarkPaidAsync((await ItemOf(food, _a)).Id, _me.Id, 10000);   // A paid exactly the food share
        var drinksId = (await Db.BillCharges.AsNoTracking().FirstAsync(c => c.Title == "ค่าเครื่องดื่ม")).Id;

        var res = await _manage.CancelChargeAsync(drinksId, _me.Id);
        Assert.True(res.Ok);
        var a = await ItemOf(food, _a);
        Assert.Equal(10000, a.AmountSatang);
        Assert.Equal(ItemStatus.PAID, a.Status);          // 10000 paid of 10000 now
    }

    [Fact]
    public async Task Cancelling_a_charge_is_refused_when_someone_paid_more_than_the_new_amount()
    {
        var food = await NewBill((_a, 10000));
        var drinks = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ค่าเครื่องดื่ม", SplitMode.EQUAL, new[] { (_a.Id, 3000L) });
        await _manage.MarkPaidAsync((await ItemOf(food, _a)).Id, _me.Id, 12000);   // paid 12000 of 13000

        var res = await _manage.CancelChargeAsync(drinks.Charge.Id, _me.Id);        // new amount 10000 < paid 12000
        Assert.False(res.Ok);
        Assert.Contains("ยกหนี้", res.Message);
        Assert.Equal(13000, (await ItemOf(food, _a)).AmountSatang);                  // nothing changed
    }

    [Fact]
    public async Task Cancelling_the_only_charge_cancels_the_bill()
    {
        var r = await NewBill((_a, 10000));
        var res = await _manage.CancelChargeAsync(r.Charge.Id, _me.Id);
        Assert.True(res.Ok);
        Db.ChangeTracker.Clear();
        var bill = await Db.Bills.FirstAsync(b => b.Id == r.Bill.Id);
        Assert.Equal(BillStatus.CANCELLED, bill.Status);
        Assert.Null(bill.ScopeKey);
        Assert.False(await _bills.HasOpenBillAsync(_me.Id, _group.Id, null));
    }

    // ───────── cancel the bill / list ─────────

    [Fact]
    public async Task Cancelling_the_bill_frees_the_scope_and_warns_when_money_was_already_taken()
    {
        var r = await NewBill((_a, 10000), (_b, 10000));
        await _manage.MarkPaidAsync((await ItemOf(r, _a)).Id, _me.Id);

        var res = await _manage.CancelBillAsync(r.Bill.Id, _me.Id);
        Assert.True(res.Ok);
        Assert.Contains("คืน", res.Message);
        Assert.False(await _bills.HasOpenBillAsync(_me.Id, _group.Id, null));
        Assert.False((await _manage.CancelBillAsync(r.Bill.Id, _me.Id)).Ok);          // already cancelled

        var next = await NewBill((_a, 500));                                           // a new bill can start
        Assert.NotEqual(r.Bill.Id, next.Bill.Id);
    }

    [Fact]
    public async Task Open_bills_list_only_shows_my_open_bills()
    {
        var mine = await NewBill((_a, 10000));
        var other = AddUser("ผู้เก็บอีกคน");
        await _bills.AddChargeAsync(other.Id, _group.Id, null, "ของคนอื่น", SplitMode.EQUAL, new[] { (_a.Id, 100L) });
        var closed = await _bills.AddChargeAsync(_me.Id, null, _b.Id, "ปิดแล้ว", SplitMode.EQUAL, new[] { (_b.Id, 100L) });
        await _manage.MarkPaidAsync((await ItemOf(closed, _b)).Id, _me.Id);

        var list = await _manage.OpenBillsAsync(_me.Id);
        Assert.Single(list);
        Assert.Equal(mine.Bill.Id, list[0].Id);
    }

    // ───────── edit a charge ─────────

    [Fact]
    public async Task Editing_a_charge_changes_title_and_each_persons_amount()
    {
        var r = await NewBill((_a, 10000), (_b, 10000));
        var itemA = await ItemOf(r, _a);
        var itemB = await ItemOf(r, _b);

        var res = await _manage.EditChargeAsync(r.Charge.Id, _me.Id, "ค่าข้าวเย็น",
            new Dictionary<long, long> { [itemA.Id] = 15000, [itemB.Id] = 5000 });

        Assert.True(res.Ok);
        Db.ChangeTracker.Clear();
        Assert.Equal(15000, (await ItemOf(r, _a)).AmountSatang);
        Assert.Equal(5000, (await ItemOf(r, _b)).AmountSatang);
        var charge = await Db.BillCharges.FirstAsync(c => c.Id == r.Charge.Id);
        Assert.Equal("ค่าข้าวเย็น", charge.Title);
        Assert.Equal(20000, charge.TotalSatang);
        Assert.Equal(SplitMode.CUSTOM, charge.SplitMode);
    }

    [Fact]
    public async Task Editing_respects_what_was_already_paid_and_who_may_edit()
    {
        var r = await NewBill((_a, 10000), (_b, 10000));
        var itemA = await ItemOf(r, _a);
        await _manage.MarkPaidAsync(itemA.Id, _me.Id);                       // a paid 100 in full

        Assert.False((await _manage.EditChargeAsync(r.Charge.Id, _me.Id, null, new Dictionary<long, long> { [itemA.Id] = 5000 })).Ok);   // below what a paid
        Assert.False((await _manage.EditChargeAsync(r.Charge.Id, _stranger.Id, "x", null)).Ok);                                         // not the creator
        Assert.False((await _manage.EditChargeAsync(r.Charge.Id, _me.Id, null, null)).Ok);                                              // nothing to change

        // raising the amount puts a fully paid person back to "still owes"
        Assert.True((await _manage.EditChargeAsync(r.Charge.Id, _me.Id, null, new Dictionary<long, long> { [itemA.Id] = 12000 })).Ok);
        var after = await ItemOf(r, _a);
        Assert.Equal(ItemStatus.PENDING, after.Status);
        Assert.Equal(2000, after.AmountSatang - after.PaidSatang);
    }

    [Fact]
    public void Slip_inspector_reads_png_and_jpeg_sizes()
    {
        var png = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        new byte[] { 0, 0, 2, 0x58, 0, 0, 3, 0x20 }.CopyTo(png, 16);          // 600 x 800
        Assert.Equal((600, 800), SlipInspector.TryGetSize(png));

        var jpg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0x03, 0x20, 0x02, 0x58, 3, 0, 0, 0 };   // 600 x 800
        Assert.Equal((600, 800), SlipInspector.TryGetSize(jpg));
        Assert.Null(SlipInspector.TryGetSize(new byte[] { 1, 2, 3 }));
    }
}
