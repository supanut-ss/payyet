using PayYet.Data;
using PayYet.Domain;
using PayYet.Services;

namespace PayYet.Tests;

public class VerbSemanticsTests
{
    [Fact]
    public void Collect_is_per_person_and_split_divides_a_total()
    {
        Assert.True(QuickEntryParser.Parse("เก็บ ค่าอาหาร 200")!.PerPerson);
        Assert.False(QuickEntryParser.Parse("หาร ค่าเครื่องดื่ม 300")!.PerPerson);
        Assert.False(QuickEntryParser.Parse("หารเท่า 300 ค่าข้าว")!.PerPerson);
        Assert.False(QuickEntryParser.Parse("เก็บ 500 หาร")!.PerPerson);       // the keyword can flip the mode either way
        Assert.True(QuickEntryParser.Parse("หาร 500 คนละ")!.PerPerson);
    }

    [Fact]
    public void Title_may_come_before_the_amount_and_everyone_is_recognised()
    {
        var p = QuickEntryParser.Parse("เก็บ ค่าอาหาร 200")!;
        Assert.Equal(TokenKind.Word, p.Tokens[0].Kind);
        Assert.Equal("ค่าอาหาร", p.Tokens[0].Text);
        Assert.Equal(200m, p.Tokens[1].Value);
        Assert.False(p.Everyone);

        Assert.True(QuickEntryParser.Parse("หาร 300 ค่าข้าว ทุกคน")!.Everyone);
        Assert.True(QuickEntryParser.Parse("หาร 300 ทั้งกลุ่ม")!.Everyone);
    }
}

public class PickerFlowTests : DbTestBase
{
    private readonly AppUser _me, _a, _b, _c, _outsider;
    private readonly ChatGroup _group, _other;

    public PickerFlowTests()
    {
        _me = AddUser("ฉัน", promptPay: "0812345678");
        _a = AddUser("เอ");
        _b = AddUser("บี");
        _c = AddUser("ซี");
        _outsider = AddUser("คนนอก");                 // shares no group with the collector
        _group = AddGroup("กลุ่มหลัก", _me, _a, _b);
        _other = AddGroup("กลุ่มรอง", _me, _c);
    }

    [Fact]
    public async Task Commands_without_names_ask_the_collector_to_pick()
    {
        var svc = new DraftService(Db);

        var split = await svc.BuildAsync(_me, "หาร ค่าเครื่องดื่ม 300");
        Assert.True(split.NeedPick);
        Assert.Equal("ค่าเครื่องดื่ม", split.Draft!.Title);
        Assert.Equal(30000, split.Draft.AmountSatang);
        Assert.False(split.Draft.PerPerson);

        var single = await svc.BuildAsync(_me, "เก็บ ค่าอาหาร 200");
        Assert.True(single.NeedPick);
        Assert.True(single.Draft!.PerPerson);
        Assert.Equal(20000, single.Draft.AmountSatang);

        Assert.False((await svc.BuildAsync(_me, "หาร 300 ทุกคน")).NeedPick);       // "everyone" skips the picker
        Assert.False((await svc.BuildAsync(_me, "เก็บ 200 เอ")).NeedPick);         // a typed name skips it too
        Assert.NotNull((await svc.BuildAsync(_me, "หาร ค่าเครื่องดื่ม")).Error);    // no amount at all
    }

    [Fact]
    public async Task Typed_in_a_group_the_picker_is_always_used_and_remembers_the_group()
    {
        var svc = new DraftService(Db);

        var withName = await svc.BuildAsync(_me, "เก็บ ค่าข้าว 200 เอ", _group.Id, alwaysPick: true);
        Assert.True(withName.NeedPick);                       // names are chosen on the page, even if one was typed
        Assert.Equal(_group.Id, withName.Draft!.GroupId);     // the page opens on this group
        Assert.Equal(20000, withName.Draft.AmountSatang);

        var plain = await svc.BuildAsync(_me, "หาร ค่าเครื่องดื่ม 300", _group.Id, alwaysPick: true);
        Assert.True(plain.NeedPick);
        Assert.Equal(_group.Id, plain.Draft!.GroupId);

        Assert.False((await svc.BuildAsync(_me, "หาร 300 ทุกคน", _group.Id, alwaysPick: true)).NeedPick);   // explicit "everyone" is honoured
    }

    [Fact]
    public async Task Edited_amounts_make_a_custom_bill_and_are_validated()
    {
        var svc = new DraftService(Db);
        var proto = (await svc.BuildAsync(_me, "หาร ค่าข้าว 300")).Draft!;
        var ids = new[] { _a.Id, _b.Id };

        var ok = await svc.BuildFromSelectionAsync(_me, proto, ids, "P", new Dictionary<long, decimal> { [_a.Id] = 200m, [_b.Id] = 100m });
        Assert.True(ok.Draft!.Custom);
        Assert.Equal(new long[] { 20000, 10000 }, ok.Draft.ComputeShares().Select(x => x.Satang).ToArray());

        Assert.NotNull((await svc.BuildFromSelectionAsync(_me, proto, ids, "P", new Dictionary<long, decimal> { [_a.Id] = 200m })).Error);                      // someone missing
        Assert.NotNull((await svc.BuildFromSelectionAsync(_me, proto, ids, "P", new Dictionary<long, decimal> { [_a.Id] = 0m, [_b.Id] = 100m })).Error);        // zero amount
        Assert.False((await svc.BuildFromSelectionAsync(_me, proto, ids, "P")).Draft!.Custom);                                                                   // untouched = equal split
    }

    [Fact]
    public async Task Tagged_people_are_exact_and_exclusion_means_everyone_else()
    {
        var svc = new DraftService(Db);

        var only = await svc.BuildFromTagsAsync(_me, "เก็บ ค่าข้าว 200", _group.Id, new[] { _a.LineUserId }, exclude: false);
        Assert.Null(only.Error);
        Assert.Equal(new[] { _a.Id }, only.Draft!.Debtors.Select(d => d.UserId).ToArray());
        Assert.Equal("G", only.Draft.Dest);
        Assert.Equal("ค่าข้าว", only.Draft.Title);

        var rest = await svc.BuildFromTagsAsync(_me, "หาร ค่าข้าว 300 ยกเว้น", _group.Id, new[] { _a.LineUserId }, exclude: true);
        Assert.Equal(new[] { _b.Id }, rest.Draft!.Debtors.Select(d => d.UserId).ToArray());   // group has me, a, b → everyone but me and a

        Assert.NotNull((await svc.BuildFromTagsAsync(_me, "เก็บ ค่าข้าว 200", _group.Id, new[] { _outsider.LineUserId }, exclude: false)).Error);   // not a known member
        Assert.NotNull((await svc.BuildFromTagsAsync(_me, "เก็บ ค่าข้าว", _group.Id, new[] { _a.LineUserId }, exclude: false)).Error);                 // no amount
    }

    [Fact]
    public async Task Total_is_divided_by_the_number_of_people_picked()
    {
        var svc = new DraftService(Db);
        var proto = (await svc.BuildAsync(_me, "หาร ค่าเครื่องดื่ม 300")).Draft!;

        var two = (await svc.BuildFromSelectionAsync(_me, proto, new[] { _a.Id, _b.Id }, "G:" + _group.Id)).Draft!;
        Assert.Equal("G", two.Dest);
        Assert.Equal(_group.Id, two.GroupId);
        Assert.Equal(new long[] { 15000, 15000 }, two.ComputeShares().Select(s => s.Satang).ToArray());

        var three = (await svc.BuildFromSelectionAsync(_me, proto, new[] { _a.Id, _b.Id, _c.Id }, "P")).Draft!;
        Assert.Equal("P", three.Dest);
        Assert.Equal(30000, three.Total);
        Assert.All(three.ComputeShares(), s => Assert.Equal(10000, s.Satang));
    }

    [Fact]
    public async Task Collect_charges_the_full_amount_to_each_person_picked()
    {
        var svc = new DraftService(Db);
        var proto = (await svc.BuildAsync(_me, "เก็บ ค่าอาหาร 200")).Draft!;
        var one = (await svc.BuildFromSelectionAsync(_me, proto, new[] { _a.Id }, "P")).Draft!;
        Assert.Equal(20000, one.Total);
        var two = (await svc.BuildFromSelectionAsync(_me, proto, new[] { _a.Id, _b.Id }, "P")).Draft!;
        Assert.Equal(40000, two.Total);
    }

    [Fact]
    public async Task Selection_is_validated_on_the_server()
    {
        var svc = new DraftService(Db);
        var proto = (await svc.BuildAsync(_me, "หาร ค่าเครื่องดื่ม 300")).Draft!;

        Assert.NotNull((await svc.BuildFromSelectionAsync(_me, proto, Array.Empty<long>(), "P")).Error);                    // nobody picked
        Assert.NotNull((await svc.BuildFromSelectionAsync(_me, proto, new[] { _outsider.Id }, "P")).Error);                 // shares no group with the collector
        Assert.NotNull((await svc.BuildFromSelectionAsync(_me, proto, new[] { _a.Id, _c.Id }, "G:" + _group.Id)).Error);    // _c is not in that group
        Assert.NotNull((await svc.BuildFromSelectionAsync(_me, proto, new[] { _a.Id }, "G:99999")).Error);                 // unknown group
    }

    [Fact]
    public async Task Picker_lists_group_members_but_not_strangers_or_the_collector()
    {
        var cand = await new DraftService(Db).GetCandidatesAsync(_me);
        Assert.Equal(2, cand.Groups.Count);
        Assert.DoesNotContain(cand.Groups.SelectMany(g => g.Members), p => p.Id == _me.Id);
        Assert.DoesNotContain(cand.Groups.SelectMany(g => g.Members), p => p.Id == _outsider.Id);
        Assert.Equal(new[] { _a.Id, _b.Id }.OrderBy(x => x), cand.Groups.Single(g => g.Id == _group.Id).Members.Select(m => m.Id).OrderBy(x => x));

        var nobody = AddUser("ไม่มีกลุ่ม");
        Assert.Empty((await new DraftService(Db).GetCandidatesAsync(nobody)).Groups);
    }
}
