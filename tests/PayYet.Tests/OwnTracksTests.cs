using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Controllers;
using PayYet.Services;

namespace PayYet.Tests;

public class OwnTracksTests : DbTestBase
{
    private OwnTracksController NewController(string token, Action<HttpRequest>? setup = null)
    {
        var settings = new AppSettings { OwnTracks = { Token = token } };
        var c = new OwnTracksController(Microsoft.Extensions.Options.Options.Create(settings), Db, new PayYet.Services.LocationRequests())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        setup?.Invoke(c.Request);
        return c;
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task A_location_with_the_right_key_is_stored_under_the_X_Limit_U_name()
    {
        var c = NewController("secret", r =>
        {
            r.QueryString = new QueryString("?key=secret");
            r.Headers["X-Limit-U"] = "mama";
        });

        var result = await c.Receive(Json("""{"_type":"location","tid":"ma","lat":13.75,"lon":100.5,"acc":12,"batt":87,"tst":1760000000}"""));

        Assert.IsType<OkObjectResult>(result);
        var ping = await Db.LocationPings.SingleAsync();
        Assert.Equal("mama", ping.Name);
        Assert.Equal(13.75, ping.Lat);
        Assert.Equal(87, ping.Battery);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000).UtcDateTime, ping.RecordedAt);
    }

    [Fact]
    public async Task Basic_auth_password_works_and_the_name_falls_back_to_the_tracker_id()
    {
        var c = NewController("secret", r => r.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("anyone:secret")));

        await c.Receive(Json("""{"_type":"location","tid":"da","lat":1,"lon":2,"tst":1760000000}"""));

        Assert.Equal("da", (await Db.LocationPings.SingleAsync()).Name);
    }

    [Fact]
    public async Task A_wrong_or_missing_token_is_refused_and_nothing_is_stored()
    {
        var wrong = NewController("secret", r => r.QueryString = new QueryString("?key=nope"));
        var missing = NewController("secret");

        Assert.IsType<UnauthorizedResult>(await wrong.Receive(Json("""{"_type":"location","lat":1,"lon":2}""")));
        Assert.IsType<UnauthorizedResult>(await missing.Receive(Json("""{"_type":"location","lat":1,"lon":2}""")));
        Assert.Empty(await Db.LocationPings.ToListAsync());
    }

    [Fact]
    public async Task An_unconfigured_token_disables_the_endpoint()
    {
        var c = NewController("", r => r.QueryString = new QueryString("?key="));

        var result = await c.Receive(Json("""{"_type":"location","lat":1,"lon":2}"""));

        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("""{"_type":"waypoint","lat":1,"lon":2}""")]
    [InlineData("""{"_type":"location","lat":91,"lon":2}""")]
    [InlineData("""{"_type":"location","lat":"x","lon":2}""")]
    [InlineData("""[1,2]""")]
    public async Task Other_message_types_and_bad_coordinates_are_acknowledged_but_not_stored(string body)
    {
        var c = NewController("secret", r => r.QueryString = new QueryString("?key=secret"));

        Assert.IsType<OkObjectResult>(await c.Receive(Json(body)));
        Assert.Empty(await Db.LocationPings.ToListAsync());
    }
}

public class OwnTracksLatestTests : DbTestBase
{
    private OwnTracksController NewController(string query = "?key=secret")
    {
        var c = new OwnTracksController(Microsoft.Extensions.Options.Options.Create(new AppSettings { OwnTracks = { Token = "secret" } }), Db, new PayYet.Services.LocationRequests())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        c.Request.QueryString = new QueryString(query);
        return c;
    }

    private void Ping(string name, double lat, DateTime at) =>
        Db.LocationPings.Add(new PayYet.Data.LocationPing { Name = name, Lat = lat, Lon = 100, RecordedAt = at, ReceivedAt = at });

    [Fact]
    public async Task Returns_only_the_newest_ping_of_each_person()
    {
        var t = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
        Ping("mama", 1, t); Ping("mama", 2, t.AddMinutes(5)); Ping("mama", 3, t.AddMinutes(-5));
        Ping("papa", 9, t);
        await Db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewController().Latest(null));

        var json = JsonSerializer.Serialize(ok.Value);
        var rows = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("mama", rows[0].GetProperty("name").GetString());
        Assert.Equal(2, rows[0].GetProperty("lat").GetDouble());
        Assert.Equal(9, rows[1].GetProperty("lat").GetDouble());
    }

    [Fact]
    public async Task The_name_filter_narrows_to_one_person()
    {
        var t = DateTime.UtcNow;
        Ping("mama", 1, t); Ping("papa", 2, t);
        await Db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewController("?key=secret&name=papa").Latest("papa"));

        Assert.Single((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public async Task A_wrong_key_is_refused()
    {
        Assert.IsType<UnauthorizedResult>(await NewController("?key=nope").Latest(null));
    }
}

public class OwnTracksDeleteTests : DbTestBase
{
    private OwnTracksController NewController(string query)
    {
        var c = new OwnTracksController(Microsoft.Extensions.Options.Options.Create(new AppSettings { OwnTracks = { Token = "secret" } }), Db, new PayYet.Services.LocationRequests())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        c.Request.QueryString = new QueryString(query);
        return c;
    }

    private async Task Seed()
    {
        var t = DateTime.UtcNow;
        foreach (var n in new[] { "test", "test", "mama" })
            Db.LocationPings.Add(new PayYet.Data.LocationPing { Name = n, Lat = 1, Lon = 2, RecordedAt = t, ReceivedAt = t });
        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Deletes_only_the_named_persons_pings()
    {
        await Seed();

        var ok = Assert.IsType<OkObjectResult>(await NewController("?key=secret&name=test").DeletePerson("test"));

        Assert.Contains("deleted = 2", ok.Value!.ToString());
        Assert.Equal("mama", (await Db.LocationPings.SingleAsync()).Name);
    }

    [Fact]
    public async Task A_missing_name_or_wrong_key_deletes_nothing()
    {
        await Seed();

        Assert.IsType<BadRequestObjectResult>(await NewController("?key=secret").DeletePerson(null));
        Assert.IsType<UnauthorizedResult>(await NewController("?key=nope&name=test").DeletePerson("test"));
        Assert.Equal(3, await Db.LocationPings.CountAsync());
    }
}

public class OwnTracksRequestTests : DbTestBase
{
    private readonly LocationRequests _requests = new();

    private OwnTracksController NewController(string query = "?key=secret", string? user = null)
    {
        var c = new OwnTracksController(Microsoft.Extensions.Options.Options.Create(new AppSettings { OwnTracks = { Token = "secret" } }), Db, _requests)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        c.Request.QueryString = new QueryString(query);
        if (user != null) c.Request.Headers["X-Limit-U"] = user;
        return c;
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private async Task SeedPing(string name)
    {
        Db.LocationPings.Add(new PayYet.Data.LocationPing { Name = name, Lat = 1, Lon = 2, RecordedAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow });
        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_request_is_handed_to_that_phone_once_in_the_reply_to_its_next_post()
    {
        await SeedPing("mama");

        Assert.IsType<OkObjectResult>(await NewController().RequestLocation("mama"));

        // someone else posting gets nothing
        var other = Assert.IsType<OkObjectResult>(await NewController(user: "papa").Receive(Json("""{"_type":"location","lat":1,"lon":2}""")));
        Assert.Empty((object[])other.Value!);

        var first = Assert.IsType<OkObjectResult>(await NewController(user: "mama").Receive(Json("""{"_type":"location","lat":1,"lon":2}""")));
        Assert.Contains("reportLocation", JsonSerializer.Serialize(first.Value));
        var second = Assert.IsType<OkObjectResult>(await NewController(user: "mama").Receive(Json("""{"_type":"location","lat":1,"lon":2}""")));
        Assert.Empty((object[])second.Value!);
    }

    [Fact]
    public async Task The_command_is_also_delivered_on_non_location_messages_and_latest_shows_it_pending()
    {
        await SeedPing("mama");
        await NewController().RequestLocation("mama");

        var latest = Assert.IsType<OkObjectResult>(await NewController().Latest(null));
        Assert.True(JsonDocument.Parse(JsonSerializer.Serialize(latest.Value)).RootElement[0].GetProperty("requested").GetBoolean());

        var reply = Assert.IsType<OkObjectResult>(await NewController(user: "mama").Receive(Json("""{"_type":"lwt"}""")));
        Assert.Contains("reportLocation", JsonSerializer.Serialize(reply.Value));
        Assert.False(_requests.IsPending("mama"));
    }

    [Fact]
    public async Task Unknown_names_missing_names_and_wrong_keys_are_refused()
    {
        await SeedPing("mama");

        Assert.IsType<NotFoundResult>(await NewController().RequestLocation("ghost"));
        Assert.IsType<BadRequestObjectResult>(await NewController().RequestLocation(null));
        Assert.IsType<UnauthorizedResult>(await NewController("?key=nope").RequestLocation("mama"));
        Assert.False(_requests.IsPending("mama"));
    }
}

public class OwnTracksHistoryTests : DbTestBase
{
    private OwnTracksController NewController(string query = "?key=secret")
    {
        var c = new OwnTracksController(Microsoft.Extensions.Options.Options.Create(new AppSettings { OwnTracks = { Token = "secret" } }), Db, new LocationRequests())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        c.Request.QueryString = new QueryString(query);
        return c;
    }

    private void Ping(string name, DateTime utc, double lat = 1) =>
        Db.LocationPings.Add(new PayYet.Data.LocationPing { Name = name, Lat = lat, Lon = 2, RecordedAt = utc, ReceivedAt = utc });

    private static JsonElement Body(IActionResult r) => JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(r).Value)).RootElement;

    [Fact]
    public async Task Returns_one_bangkok_day_of_one_person_oldest_first()
    {
        // 2026-10-19 in Bangkok is 2026-10-18T17:00Z .. 2026-10-19T17:00Z
        Ping("mama", new DateTime(2026, 10, 18, 16, 59, 0, DateTimeKind.Utc), 9);   // the day before (Bangkok 23:59 on the 18th)
        Ping("mama", new DateTime(2026, 10, 19, 5, 0, 0, DateTimeKind.Utc), 3);
        Ping("mama", new DateTime(2026, 10, 18, 17, 0, 0, DateTimeKind.Utc), 1);     // exactly Bangkok midnight
        Ping("mama", new DateTime(2026, 10, 19, 17, 0, 0, DateTimeKind.Utc), 8);    // the day after
        Ping("papa", new DateTime(2026, 10, 19, 5, 0, 0, DateTimeKind.Utc), 7);
        await Db.SaveChangesAsync();

        var json = Body(await NewController().History("mama", "2026-10-19"));

        Assert.Equal(2, json.GetProperty("total").GetInt32());
        var pts = json.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(new[] { 1.0, 3.0 }, pts.Select(p => p.GetProperty("lat").GetDouble()).ToArray());
    }

    [Fact]
    public async Task A_dense_day_is_thinned_but_keeps_the_first_and_last_points()
    {
        var t = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3000; i++) Ping("mama", t.AddSeconds(i), i);
        await Db.SaveChangesAsync();

        var json = Body(await NewController().History("mama", "2026-10-19"));

        var pts = json.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(3000, json.GetProperty("total").GetInt32());
        Assert.Equal(1500, pts.Count);
        Assert.Equal(0, pts[0].GetProperty("lat").GetDouble());
        Assert.Equal(2999, pts[^1].GetProperty("lat").GetDouble());
    }

    [Fact]
    public async Task Bad_input_and_wrong_keys_are_refused()
    {
        Assert.IsType<BadRequestObjectResult>(await NewController().History(null, "2026-10-19"));
        Assert.IsType<BadRequestObjectResult>(await NewController().History("mama", "19/10/2026"));
        Assert.IsType<UnauthorizedResult>(await NewController("?key=nope").History("mama", "2026-10-19"));
    }
}

public class OwnTracksHardeningTests : DbTestBase
{
    private OwnTracksController NewController(string? user = null)
    {
        var c = new OwnTracksController(Microsoft.Extensions.Options.Options.Create(new AppSettings { OwnTracks = { Token = "secret" } }), Db, new LocationRequests())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        c.Request.QueryString = new QueryString("?key=secret");
        if (user != null) c.Request.Headers["X-Limit-U"] = user;
        return c;
    }

    [Fact]
    public async Task The_same_report_sent_twice_is_stored_once()
    {
        var body = JsonDocument.Parse("""{"_type":"location","lat":13.7,"lon":100.5,"tst":1760000000}""").RootElement;

        await NewController("mama").Receive(body);
        await NewController("mama").Receive(body);
        await NewController("papa").Receive(body);   // another person with the same moment is a different report

        Assert.Equal(2, await Db.LocationPings.CountAsync());
    }

    [Fact]
    public async Task Latest_returns_the_newest_ping_per_person_even_with_many_old_pings()
    {
        var t = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 200; i++)
        {
            Db.LocationPings.Add(new PayYet.Data.LocationPing { Name = "mama", Lat = i, Lon = 1, RecordedAt = t.AddMinutes(i), ReceivedAt = t });
            Db.LocationPings.Add(new PayYet.Data.LocationPing { Name = "papa", Lat = -i, Lon = 1, RecordedAt = t.AddMinutes(i), ReceivedAt = t });
        }
        await Db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewController().Latest(null));

        var rows = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement.EnumerateArray().ToList();
        Assert.Equal(new[] { "mama", "papa" }, rows.Select(r => r.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(199, rows[0].GetProperty("lat").GetDouble());
        Assert.Equal(-199, rows[1].GetProperty("lat").GetDouble());
    }

    [Fact]
    public async Task Retention_purges_only_pings_older_than_the_limit()
    {
        var now = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        Db.LocationPings.Add(new PayYet.Data.LocationPing { Name = "old", Lat = 1, Lon = 1, RecordedAt = now, ReceivedAt = now.AddDays(-91) });
        Db.LocationPings.Add(new PayYet.Data.LocationPing { Name = "new", Lat = 1, Lon = 1, RecordedAt = now, ReceivedAt = now.AddDays(-89) });
        await Db.SaveChangesAsync();

        Assert.Equal(0, await LocationRetention.PurgeAsync(Db, 0, now));   // 0 keeps everything
        Assert.Equal(1, await LocationRetention.PurgeAsync(Db, 90, now));

        Assert.Equal("new", (await Db.LocationPings.SingleAsync()).Name);
    }
}
