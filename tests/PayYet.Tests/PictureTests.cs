using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayYet.Data;
using PayYet.Services;

namespace PayYet.Tests;

public class PictureTests : DbTestBase
{
    private MemberService NewMembers() => new(Db, NewLine(), Options, NullLogger<MemberService>.Instance);

    [Fact]
    public async Task New_users_and_groups_keep_the_picture_LINE_returns()
    {
        var members = NewMembers();
        var (user, group) = await members.TouchAsync("Cgroup1", "Uabc");

        Assert.Equal("https://pic.example/Uabc.jpg", user.PictureUrl);
        Assert.Equal("https://pic.example/group.jpg", group.PictureUrl);
    }

    [Fact]
    public async Task A_user_without_a_photo_is_stored_as_empty_and_not_asked_again()
    {
        var members = NewMembers();
        var (user, _) = await members.TouchAsync("Cgroup1", "Unopic1");
        Assert.Equal("", user.PictureUrl);     // "" = LINE answered, there is no picture

        var callsBefore = Handler.Calls.Count;
        await members.TouchAsync("Cgroup1", "Unopic1");   // second message from the same person
        Assert.DoesNotContain(Handler.Calls.Skip(callsBefore), c => c.Url.Contains("/member/Unopic1"));
    }

    [Fact]
    public async Task Backfill_fills_missing_pictures_once_for_everything_the_collector_can_pick()
    {
        var me = AddUser("ฉัน");
        var a = AddUser("เอ");
        var b = AddUser("บี");
        var g = AddGroup("กลุ่ม", me, a, b);
        Assert.Null(a.PictureUrl);
        Assert.Null(g.PictureUrl);

        var members = NewMembers();
        await members.BackfillPicturesAsync(me.Id);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ToListAsync();
        Assert.All(reloaded, u => Assert.StartsWith("https://pic.example/", u.PictureUrl));
        Assert.Equal("https://pic.example/group.jpg", (await Db.Groups.FirstAsync(x => x.Id == g.Id)).PictureUrl);

        var calls = Handler.Calls.Count;
        await members.BackfillPicturesAsync(me.Id);          // nothing missing now → no LINE calls
        Assert.Equal(calls, Handler.Calls.Count);
    }

    [Fact]
    public async Task Picker_candidates_carry_pictures_and_hide_empty_ones()
    {
        var me = AddUser("ฉัน");
        var a = AddUser("เอ"); a.PictureUrl = "https://pic.example/a.jpg";
        var b = AddUser("บี"); b.PictureUrl = "";            // has no photo
        var g = AddGroup("กลุ่ม", me, a, b);
        g.PictureUrl = "https://pic.example/g.jpg";
        await Db.SaveChangesAsync();

        var cand = await new DraftService(Db).GetCandidatesAsync(me);
        var group = cand.Groups.Single();
        Assert.Equal("https://pic.example/g.jpg", group.PictureUrl);
        Assert.Equal("https://pic.example/a.jpg", group.Members.Single(m => m.Name == "เอ").PictureUrl);
        Assert.Null(group.Members.Single(m => m.Name == "บี").PictureUrl);   // the page shows an initial instead
    }
}
