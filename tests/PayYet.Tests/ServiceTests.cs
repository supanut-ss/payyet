using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;
using PayYet.Services;

namespace PayYet.Tests;

/// <summary>Records every LINE call instead of sending it.</summary>
public class FakeLineHandler : HttpMessageHandler
{
    public List<(string Url, string Body)> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Calls.Add((url, request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct)));
        var body = "{}";
        if (url.Contains("/summary")) body = "{\"groupId\":\"C1\",\"groupName\":\"Group\",\"pictureUrl\":\"https://pic.example/group.jpg\"}";
        else if (url.Contains("/member/") || url.Contains("/profile/"))
        {
            var userId = url[(url.LastIndexOf('/') + 1)..];
            body = userId.StartsWith("Unopic")
                ? "{\"userId\":\"" + userId + "\",\"displayName\":\"No Pic\"}"
                : "{\"userId\":\"" + userId + "\",\"displayName\":\"Someone\",\"pictureUrl\":\"https://pic.example/" + userId + ".jpg\"}";
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    public int Pushes(string to) => Calls.Count(c => c.Url.EndsWith("/message/push") && c.Body.Contains($"\"to\":\"{to}\""));
}

/// <summary>SQLite in-memory database built from the EF model (checks relationships and the business logic, not MySQL specifics).</summary>
public abstract class DbTestBase : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    protected AppDbContext Db { get; }
    protected FakeLineHandler Handler { get; } = new();
    protected IOptions<AppSettings> Options { get; } = Microsoft.Extensions.Options.Options.Create(new AppSettings
    {
        Liff = { Id = "liff-test" },
        App = { PublicBaseUrl = "https://example.test/lineapi" },
        Jobs = { ReminderHour = 0 }
    });

    protected DbTestBase()
    {
        _conn.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        Db.Database.EnsureCreated();
    }

    protected LineClient NewLine() => new(new HttpClient(Handler), Options, NullLogger<LineClient>.Instance);

    protected AppUser AddUser(string name, bool friend = true, string? promptPay = null)
    {
        var u = new AppUser { LineUserId = "U" + Guid.NewGuid().ToString("N"), DisplayName = name, IsFriend = friend, PromptPayId = promptPay, CreatedAt = DateTime.UtcNow };
        Db.Users.Add(u);
        Db.SaveChanges();
        return u;
    }

    protected ChatGroup AddGroup(string name, params AppUser[] members)
    {
        var g = new ChatGroup { LineGroupId = "C" + Guid.NewGuid().ToString("N"), Name = name, Active = true };
        Db.Groups.Add(g);
        Db.SaveChanges();
        foreach (var m in members) Db.GroupMembers.Add(new GroupMember { GroupId = g.Id, UserId = m.Id, JoinedAt = DateTime.UtcNow, Source = MemberSource.SEEN });
        Db.SaveChanges();
        return g;
    }

    public void Dispose() { Db.Dispose(); _conn.Dispose(); }
}

public class DraftServiceTests : DbTestBase
{
    private readonly AppUser _me, _somchai, _malee, _nid;
    private readonly ChatGroup _group;

    public DraftServiceTests()
    {
        _me = AddUser("ฉัน", promptPay: "0812345678");
        _somchai = AddUser("สมชาย");
        _malee = AddUser("มาลี");
        _nid = AddUser("นิด");
        _group = AddGroup("เพื่อนสนิท", _me, _somchai, _malee, _nid);
    }

    [Fact]
    public async Task Total_is_split_equally_among_everyone_except_the_collector()
    {
        var r = await new DraftService(Db).BuildAsync(_me, "หาร 900 ค่าข้าว ทุกคน");
        var d = r.Draft!;
        Assert.Equal("G", d.Dest);
        Assert.Equal(_group.Id, d.GroupId);
        Assert.Equal("ค่าข้าว", d.Title);
        Assert.Equal(3, d.Debtors.Count);
        Assert.All(d.ComputeShares(), s => Assert.Equal(30000, s.Satang));
    }

    [Fact]
    public async Task Per_person_keyword_charges_each_person_the_full_amount()
    {
        var d = (await new DraftService(Db).BuildAsync(_me, "เก็บ 100 ทุกคน")).Draft!;
        Assert.Equal(30000, d.Total);
        Assert.All(d.ComputeShares(), s => Assert.Equal(10000, s.Satang));
    }

    [Fact]
    public async Task Excluded_people_are_removed()
    {
        var d = (await new DraftService(Db).BuildAsync(_me, "หาร 600 ยกเว้น สมชาย")).Draft!;
        Assert.Equal(2, d.Debtors.Count);
        Assert.DoesNotContain(d.Debtors, x => x.UserId == _somchai.Id);
        Assert.All(d.ComputeShares(), s => Assert.Equal(30000, s.Satang));
    }

    [Fact]
    public async Task Name_amount_pairs_become_custom_amounts()
    {
        var d = (await new DraftService(Db).BuildAsync(_me, "เก็บ สมชาย 200 มาลี 300")).Draft!;
        Assert.True(d.Custom);
        Assert.Equal("P", d.Dest);
        Assert.Equal(new long[] { 20000, 30000 }, d.ComputeShares().Select(s => s.Satang).ToArray());
    }

    [Fact]
    public async Task A_single_name_makes_a_private_bill()
    {
        var d = (await new DraftService(Db).BuildAsync(_me, "เก็บ 200 สมชาย ค่าน้ำ")).Draft!;
        Assert.Equal("P", d.Dest);
        Assert.Single(d.Debtors);
        Assert.Equal("ค่าน้ำ", d.Title);
        Assert.Equal(20000, d.Total);
    }

    [Fact]
    public async Task Several_groups_ask_which_one_unless_named_or_last_used()
    {
        var other = AddGroup("ทีมงาน", _me, _somchai);
        var svc = new DraftService(Db);

        var ask = await svc.BuildAsync(_me, "เก็บ 100 ทุกคน");
        Assert.NotNull(ask.AskGroups);
        Assert.Equal(2, ask.AskGroups!.Count);

        var named = (await svc.BuildAsync(_me, "เก็บ 100 ทีมงาน")).Draft!;
        Assert.Equal(other.Id, named.GroupId);
        Assert.Single(named.Debtors);

        _me.LastGroupId = _group.Id;
        Assert.Equal(_group.Id, (await svc.BuildAsync(_me, "เก็บ 100 ทุกคน")).Draft!.GroupId);

        var forced = (await svc.BuildAsync(_me, "เก็บ 100", other.Id)).Draft!;
        Assert.Equal(other.Id, forced.GroupId);
    }

    [Fact]
    public async Task Errors_are_reported_in_plain_language()
    {
        var svc = new DraftService(Db);
        Assert.Contains("ไม่พบยอดเงิน", (await svc.BuildAsync(_me, "เก็บ ค่าข้าว")).Error);
        Assert.Contains("ไม่พบชื่อ", (await svc.BuildAsync(_me, "เก็บ 500 ยกเว้น ใครก็ไม่รู้")).Error);
        var stranger = AddUser("คนแปลกหน้า");
        Assert.Contains("ชวนบอทเข้ากลุ่ม", (await svc.BuildAsync(stranger, "เก็บ 100")).Error);
    }
}

public class BillServiceTests : DbTestBase
{
    private readonly AppUser _me, _a, _b;
    private readonly ChatGroup _group;
    private readonly BillService _bills;

    public BillServiceTests()
    {
        _me = AddUser("ฉัน");
        _a = AddUser("เอ");
        _b = AddUser("บี");
        _group = AddGroup("กลุ่ม", _me, _a, _b);
        _bills = new BillService(Db);
    }

    private static (long, long)[] Shares(AppUser a, long x, AppUser b, long y) => new[] { (a.Id, x), (b.Id, y) };

    [Fact]
    public async Task Second_charge_merges_into_the_open_bill_and_totals_add_up()
    {
        var first = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ข้าว", SplitMode.EQUAL, Shares(_a, 10000, _b, 10000));
        var second = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "น้ำ", SplitMode.EQUAL, Shares(_a, 5000, _b, 5000));

        Assert.False(first.Merged);
        Assert.True(second.Merged);
        Assert.Equal(first.Bill.Id, second.Bill.Id);
        Assert.Equal(1, await Db.Bills.CountAsync());
        Assert.Equal(2, await Db.BillCharges.CountAsync());
        Assert.All(await Db.BillItems.ToListAsync(), i => Assert.Equal(15000, i.AmountSatang));
    }

    [Fact]
    public async Task Paid_debtor_reopens_with_only_the_new_remainder()
    {
        var r = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ข้าว", SplitMode.EQUAL, Shares(_a, 10000, _b, 10000));
        var item = await Db.BillItems.FirstAsync(i => i.DebtorId == _a.Id);
        item.PaidSatang = 10000;
        item.Status = ItemStatus.PAID;
        await Db.SaveChangesAsync();

        await _bills.AddChargeAsync(_me.Id, _group.Id, null, "น้ำ", SplitMode.EQUAL, Shares(_a, 3000, _b, 3000));

        await Db.Entry(item).ReloadAsync();
        Assert.Equal(ItemStatus.PENDING, item.Status);
        Assert.Equal(3000, item.Remaining);
    }

    [Fact]
    public async Task New_debtor_in_later_charge_gets_an_item_and_closed_bill_starts_a_new_one()
    {
        var c = AddUser("ซี");
        await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ข้าว", SplitMode.EQUAL, new[] { (_a.Id, 10000L) });
        var r = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "น้ำ", SplitMode.EQUAL, new[] { (c.Id, 2000L) });
        Assert.Equal(2, await Db.BillItems.CountAsync(i => i.BillId == r.Bill.Id));

        foreach (var i in await Db.BillItems.ToListAsync()) { i.Status = ItemStatus.PAID; i.PaidSatang = i.AmountSatang; }
        await Db.SaveChangesAsync();
        Assert.True(await _bills.CloseIfDoneAsync(r.Bill));
        Assert.Null(r.Bill.ScopeKey);

        var next = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ใหม่", SplitMode.EQUAL, new[] { (_a.Id, 100L) });
        Assert.False(next.Merged);
        Assert.NotEqual(r.Bill.Id, next.Bill.Id);
    }

    [Fact]
    public async Task Individual_bills_are_scoped_per_debtor()
    {
        var x = await _bills.AddChargeAsync(_me.Id, null, _a.Id, "ยืม", SplitMode.EQUAL, new[] { (_a.Id, 5000L) });
        var y = await _bills.AddChargeAsync(_me.Id, null, _b.Id, "ยืม", SplitMode.EQUAL, new[] { (_b.Id, 5000L) });
        var z = await _bills.AddChargeAsync(_me.Id, null, _a.Id, "ยืมอีก", SplitMode.EQUAL, new[] { (_a.Id, 1000L) });
        Assert.NotEqual(x.Bill.Id, y.Bill.Id);
        Assert.Equal(x.Bill.Id, z.Bill.Id);
        Assert.Equal(6000, (await Db.BillItems.FirstAsync(i => i.BillId == x.Bill.Id)).AmountSatang);
    }

    [Fact]
    public async Task Undo_removes_the_charge_but_not_after_someone_paid()
    {
        var first = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ข้าว", SplitMode.EQUAL, Shares(_a, 10000, _b, 10000));
        var second = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "น้ำ", SplitMode.EQUAL, Shares(_a, 3000, _b, 3000));

        var (ok, _, _) = await _bills.UndoChargeAsync(second.Charge.Id, _me.Id);
        Assert.True(ok);
        Assert.All(await Db.BillItems.ToListAsync(), i => Assert.Equal(10000, i.AmountSatang));

        var other = await _bills.UndoChargeAsync(first.Charge.Id, _a.Id);   // not the creator
        Assert.False(other.Ok);

        (await Db.BillItems.FirstAsync()).PaidSatang = 100;
        await Db.SaveChangesAsync();
        Assert.False((await _bills.UndoChargeAsync(first.Charge.Id, _me.Id)).Ok);
    }

    [Fact]
    public async Task Undoing_the_only_charge_cancels_the_bill_and_frees_the_scope()
    {
        var r = await _bills.AddChargeAsync(_me.Id, _group.Id, null, "ข้าว", SplitMode.EQUAL, Shares(_a, 10000, _b, 10000));
        var (ok, _, bill) = await _bills.UndoChargeAsync(r.Charge.Id, _me.Id);
        Assert.True(ok);
        Assert.Equal(BillStatus.CANCELLED, bill!.Status);
        Assert.False(await _bills.HasOpenBillAsync(_me.Id, _group.Id, null));
        Assert.Equal(0, await Db.BillItems.CountAsync());
    }
}

public class ReminderServiceTests : DbTestBase
{
    [Fact]
    public async Task Group_digest_is_sent_once_per_day_and_not_on_the_creation_day()
    {
        var me = AddUser("ฉัน");
        var a = AddUser("เอ");
        var group = AddGroup("กลุ่ม", me, a);
        var line = NewLine();
        var bills = new BillService(Db);
        var reminders = new ReminderService(Db, line, new BillCardBuilder(Db, Options), new BillCreationService(Db, line, bills, new BillCardBuilder(Db, Options)), new DraftService(Db), Options, NullLogger<ReminderService>.Instance);

        var r = await bills.AddChargeAsync(me.Id, group.Id, null, "ข้าว", SplitMode.EQUAL, new[] { (a.Id, 10000L) });

        await reminders.RunAsync();
        Assert.Equal(0, Handler.Pushes(group.LineGroupId));   // created today → first reminder is tomorrow

        r.Bill.CreatedAt = DateTime.UtcNow.AddDays(-2);
        await Db.SaveChangesAsync();

        var first = await reminders.RunAsync();
        var second = await reminders.RunAsync();               // same day again
        Assert.Equal(1, first.GroupMessages);
        Assert.Equal(0, second.GroupMessages);
        Assert.Equal(1, Handler.Pushes(group.LineGroupId));

        Db.Bills.Single().LastRemindedOn = BangkokTime.Today.AddDays(-1);   // pretend yesterday's run
        await Db.SaveChangesAsync();
        Assert.Equal(1, (await reminders.RunAsync()).GroupMessages);
    }

    [Fact]
    public async Task Individual_bill_goes_privately_to_a_friend_only_and_paid_bills_are_skipped()
    {
        var me = AddUser("ฉัน");
        var friend = AddUser("เพื่อน", friend: true);
        var stranger = AddUser("คนไม่ได้แอดบอท", friend: false);
        var bills = new BillService(Db);
        var reminders = new ReminderService(Db, NewLine(), new BillCardBuilder(Db, Options), new BillCreationService(Db, NewLine(), bills, new BillCardBuilder(Db, Options)), new DraftService(Db), Options, NullLogger<ReminderService>.Instance);

        var b1 = await bills.AddChargeAsync(me.Id, null, friend.Id, "ยืม", SplitMode.EQUAL, new[] { (friend.Id, 5000L) });
        var b2 = await bills.AddChargeAsync(me.Id, null, stranger.Id, "ยืม", SplitMode.EQUAL, new[] { (stranger.Id, 5000L) });
        foreach (var b in Db.Bills) b.CreatedAt = DateTime.UtcNow.AddDays(-3);
        await Db.SaveChangesAsync();

        var result = await reminders.RunAsync();
        Assert.Equal(1, result.PrivateMessages);
        Assert.Equal(1, Handler.Pushes(friend.LineUserId));
        Assert.Equal(0, Handler.Pushes(stranger.LineUserId));

        var item = await Db.BillItems.FirstAsync(i => i.BillId == b1.Bill.Id);
        item.Status = ItemStatus.PAID;
        Db.Bills.First(b => b.Id == b1.Bill.Id).LastRemindedOn = null;
        await Db.SaveChangesAsync();
        Assert.Equal(0, (await reminders.RunAsync()).PrivateMessages);
    }
}
