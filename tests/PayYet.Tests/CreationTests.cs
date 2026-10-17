using Microsoft.EntityFrameworkCore;
using PayYet.Data;
using PayYet.Services;

namespace PayYet.Tests;

public class CreationTests : DbTestBase
{
    private readonly AppUser _me, _a, _b;
    private readonly ChatGroup _group;
    private readonly BillCreationService _creation;

    public CreationTests()
    {
        _me = AddUser("ฉัน", promptPay: "0812345678");
        _a = AddUser("เอ", friend: true);
        _b = AddUser("บี", friend: false);
        _group = AddGroup("กลุ่ม", _me, _a, _b);
        var bills = new BillService(Db);
        _creation = new BillCreationService(Db, NewLine(), bills, new BillCardBuilder(Db, Options));
    }

    private Draft GroupDraft(long amountSatang = 20000, bool perPerson = true) => new()
    {
        Dest = "G", GroupId = _group.Id, GroupName = _group.Name!, Title = "ค่าข้าว", PerPerson = perPerson, AmountSatang = amountSatang,
        Debtors = { new DraftDebtor { UserId = _a.Id, Name = "เอ" }, new DraftDebtor { UserId = _b.Id, Name = "บี" } }
    };

    [Fact]
    public async Task Group_bill_posts_one_card_in_the_group_and_remembers_the_group()
    {
        var created = await _creation.CreateAsync(_me, GroupDraft());

        Assert.Equal(1, Handler.Pushes(_group.LineGroupId));
        Assert.Equal(0, Handler.Pushes(_a.LineUserId));           // nothing private for a group bill
        Assert.Contains("กลุ่ม", created.Summary);
        Assert.Single(created.Undo);
        Assert.Equal(_group.Id, (await Db.Users.AsNoTracking().FirstAsync(u => u.Id == _me.Id)).LastGroupId);
        Assert.Equal(2, await Db.BillItems.CountAsync());
        Assert.All(await Db.BillItems.ToListAsync(), i => Assert.Equal(20000, i.AmountSatang));
    }

    [Fact]
    public async Task Private_bills_reach_friends_of_the_bot_and_report_the_ones_who_are_not()
    {
        var draft = GroupDraft();
        draft.Dest = "P"; draft.GroupId = null;

        var created = await _creation.CreateAsync(_me, draft);

        Assert.Equal(1, Handler.Pushes(_a.LineUserId));            // friend gets the card
        Assert.Equal(0, Handler.Pushes(_b.LineUserId));            // not a friend → cannot be messaged
        Assert.Contains("บี", created.Summary);                    // the collector is told who was not reached
        Assert.Equal(2, created.Undo.Count);
        Assert.Equal(2, await Db.Bills.CountAsync());              // one private bill per debtor
    }

    [Fact]
    public async Task Undo_removes_the_charge_and_tells_the_group_once()
    {
        var created = await _creation.CreateAsync(_me, GroupDraft());
        var pushesBefore = Handler.Pushes(_group.LineGroupId);

        var (ok, _) = await _creation.UndoAsync(_me, created.Undo[0].ChargeId);

        Assert.True(ok);
        Assert.Equal(pushesBefore + 1, Handler.Pushes(_group.LineGroupId));   // "bill cancelled" notice
        Db.ChangeTracker.Clear();
        Assert.Equal(BillStatus.CANCELLED, (await Db.Bills.FirstAsync()).Status);

        var again = await _creation.UndoAsync(_me, created.Undo[0].ChargeId);
        Assert.False(again.Ok);                                               // nothing left to undo
        Assert.False((await _creation.UndoAsync(_a, created.Undo[0].ChargeId)).Ok);   // and only the creator may
    }

    [Fact]
    public async Task Second_bill_in_the_same_group_is_a_separate_bill()
    {
        await _creation.CreateAsync(_me, GroupDraft(10000));
        await _creation.CreateAsync(_me, GroupDraft(5000));

        Assert.Equal(2, await Db.Bills.CountAsync());
        Assert.Equal(2, await Db.BillCharges.CountAsync());
        Assert.Equal(1, await Db.Bills.CountAsync(b => b.Charges.Count == 1 && b.Items.All(i => i.AmountSatang == 10000)));
        Assert.Equal(1, await Db.Bills.CountAsync(b => b.Charges.Count == 1 && b.Items.All(i => i.AmountSatang == 5000)));
    }
}
