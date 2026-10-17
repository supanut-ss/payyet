using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayYet.Controllers;
using PayYet.Data;
using PayYet.Services;

namespace PayYet.Tests;

/// <summary>The picker page's POST, called like the browser does (ID token header → verified → bill created).</summary>
public class PickEndpointTests : DbTestBase
{
    [Fact]
    public async Task Picking_people_creates_the_bill_in_the_group_and_clears_the_pick_state()
    {
        var me = AddUser("ฉัน", promptPay: "0812345678");
        var a = AddUser("เอ");
        var b = AddUser("บี");
        var group = AddGroup("กลุ่ม", me, a, b);
        Db.States.Add(new ConversationState
        {
            UserId = me.Id, Flow = "PICK", Step = "pick", ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            Data = JsonSerializer.Serialize(new Draft { Dest = "?", Title = "ค่าเปียโน", PerPerson = true, AmountSatang = 100000, GroupId = group.Id })
        });
        await Db.SaveChangesAsync();

        var line = NewLine();
        var bills = new BillService(Db);
        var creation = new BillCreationService(Db, line, bills, new BillCardBuilder(Db, Options));
        var controller = new PayController(Db, line, null!, creation, Options)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = "Bearer " + me.LineUserId;

        var result = await controller.Pick(new PayController.PickRequest(new List<long> { a.Id }, $"G:{group.Id}", null), new DraftService(Db));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        Db.ChangeTracker.Clear();
        Assert.Equal(1, await Db.Bills.CountAsync(x => x.GroupId == group.Id));
        Assert.Equal(100000, (await Db.BillItems.FirstAsync()).AmountSatang);
        Assert.Null(await Db.States.FindAsync(me.Id));
        Assert.Equal(1, Handler.Pushes(group.LineGroupId) > 0 ? 1 : 0);
    }
}
