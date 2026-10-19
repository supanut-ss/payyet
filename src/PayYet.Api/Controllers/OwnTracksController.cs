using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Services;

namespace PayYet.Controllers;

[ApiController]
public class OwnTracksController(IOptions<AppSettings> options, AppDbContext db, LocationRequests requests) : ControllerBase
{
    // OwnTracks (HTTP mode) POSTs one JSON object per report, e.g.
    // {"_type":"location","tid":"ma","lat":13.75,"lon":100.5,"acc":12,"batt":87,"tst":1760000000}
    // Auth: the token as the HTTP Basic password, or as ?key= (OwnTracks lets the URL carry a query string).
    // The person is the X-Limit-U header (OwnTracks "username"), falling back to the tracker id.
    [HttpPost("owntracks")]
    [RequestSizeLimit(16 * 1024)]   // a position report is tiny; refuse big bodies before they are parsed
    public async Task<IActionResult> Receive([FromBody] JsonElement body)
    {
        var expected = options.Value.OwnTracks.Token;
        if (string.IsNullOrEmpty(expected)) return StatusCode(503, "OwnTracks:Token is not configured");
        if (!TokenMatches(expected)) return Unauthorized();

        // OwnTracks expects 200 + a JSON array (commands/cards for the phone); other message types are acknowledged and ignored.
        var name = PersonOf(body);
        if (body.ValueKind != JsonValueKind.Object || Str(body, "_type") != "location"
            || !Num(body, "lat", out var lat) || !Num(body, "lon", out var lon)
            || lat is < -90 or > 90 || lon is < -180 or > 180)
            return Ok(CommandsFor(name));

        var now = DateTime.UtcNow;
        var recorded = Num(body, "tst", out var tst) && tst > 0 && tst < 4_102_444_800 ? DateTimeOffset.FromUnixTimeSeconds((long)tst).UtcDateTime : now;

        // OwnTracks re-sends a report after a flaky connection; the same person + moment + place is stored once.
        if (await db.LocationPings.AnyAsync(p => p.Name == name && p.RecordedAt == recorded && p.Lat == lat && p.Lon == lon))
            return Ok(CommandsFor(name));

        db.LocationPings.Add(new LocationPing
        {
            Name = name,
            Device = Cut(Request.Headers["X-Limit-D"].FirstOrDefault(), 64),
            Lat = lat,
            Lon = lon,
            Accuracy = Num(body, "acc", out var acc) ? (int)acc : null,
            Battery = Num(body, "batt", out var batt) ? (int)batt : null,
            RecordedAt = recorded,
            ReceivedAt = now,
        });
        await db.SaveChangesAsync();
        return Ok(CommandsFor(name));
    }

    // Asks one person's phone to report its position. The phone gets the command in the response of its next post
    // (OwnTracks needs "Remote commands" switched on); until then /latest shows the request as pending.
    [HttpPost("owntracks/request")]
    public async Task<IActionResult> RequestLocation([FromQuery] string? name)
    {
        var expected = options.Value.OwnTracks.Token;
        if (string.IsNullOrEmpty(expected)) return StatusCode(503, "OwnTracks:Token is not configured");
        if (!TokenMatches(expected)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("name is required");

        var n = name.Trim();
        if (!await db.LocationPings.AnyAsync(p => p.Name == n)) return NotFound();
        requests.Request(n);
        return Ok(new { name = n, requested = true });
    }

    // Latest position of each person (or just ?name=). Same token as the POST.
    [HttpGet("owntracks/latest")]
    public async Task<IActionResult> Latest([FromQuery] string? name)
    {
        var expected = options.Value.OwnTracks.Token;
        if (string.IsNullOrEmpty(expected)) return StatusCode(503, "OwnTracks:Token is not configured");
        if (!TokenMatches(expected)) return Unauthorized();

        // One indexed lookup (name, recorded_at) per person: cheap however many pings are stored.
        var names = string.IsNullOrWhiteSpace(name)
            ? await db.LocationPings.Select(p => p.Name).Distinct().OrderBy(n => n).ToListAsync()
            : [name.Trim()];
        var rows = new List<LocationPing>();
        foreach (var person in names)
        {
            var newest = await db.LocationPings.Where(p => p.Name == person)
                .OrderByDescending(p => p.RecordedAt).ThenByDescending(p => p.Id).FirstOrDefaultAsync();
            if (newest != null) rows.Add(newest);
        }
        return Ok(rows.Select(p => new
        {
            name = p.Name,
            device = p.Device,
            lat = p.Lat,
            lon = p.Lon,
            accuracy = p.Accuracy,
            battery = p.Battery,
            recordedAt = DateTime.SpecifyKind(p.RecordedAt, DateTimeKind.Utc),
            requested = requests.IsPending(p.Name),
        }));
    }

    // One person's track for one Bangkok calendar day (?date=yyyy-MM-dd, default today), oldest first.
    // A very dense day is thinned evenly to at most MaxTrackPoints, always keeping the first and last point.
    private const int MaxTrackPoints = 1500;

    [HttpGet("owntracks/history")]
    public async Task<IActionResult> History([FromQuery] string? name, [FromQuery] string? date)
    {
        var expected = options.Value.OwnTracks.Token;
        if (string.IsNullOrEmpty(expected)) return StatusCode(503, "OwnTracks:Token is not configured");
        if (!TokenMatches(expected)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("name is required");

        var day = BangkokTime.Today;
        if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day))
            return BadRequest("date must be yyyy-MM-dd");

        var from = BangkokTime.StartOfDayUtc(day);
        var to = BangkokTime.StartOfDayUtc(day.AddDays(1));
        var n = name.Trim();
        var all = await db.LocationPings.Where(p => p.Name == n && p.RecordedAt >= from && p.RecordedAt < to)
            .OrderBy(p => p.RecordedAt).ThenBy(p => p.Id).ToListAsync();

        var points = all;
        if (all.Count > MaxTrackPoints)
        {
            var step = (double)(all.Count - 1) / (MaxTrackPoints - 1);
            points = Enumerable.Range(0, MaxTrackPoints).Select(i => all[(int)Math.Round(i * step)]).ToList();
        }
        return Ok(new
        {
            name = n,
            date = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            total = all.Count,
            points = points.Select(p => new
            {
                lat = p.Lat,
                lon = p.Lon,
                accuracy = p.Accuracy,
                battery = p.Battery,
                recordedAt = DateTime.SpecifyKind(p.RecordedAt, DateTimeKind.Utc),
            }),
        });
    }

    // Removes every stored ping of one person (e.g. test data). The name is required, so a bare call cannot wipe the table.
    [HttpDelete("owntracks")]
    public async Task<IActionResult> DeletePerson([FromQuery] string? name)
    {
        var expected = options.Value.OwnTracks.Token;
        if (string.IsNullOrEmpty(expected)) return StatusCode(503, "OwnTracks:Token is not configured");
        if (!TokenMatches(expected)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("name is required");

        var n = name.Trim();
        var deleted = await db.LocationPings.Where(p => p.Name == n).ExecuteDeleteAsync();
        return Ok(new { name = n, deleted });
    }

    private string PersonOf(JsonElement body)
    {
        var name = (Request.Headers["X-Limit-U"].FirstOrDefault() ?? (body.ValueKind == JsonValueKind.Object ? Str(body, "tid") : null) ?? "").Trim();
        return name.Length == 0 ? "unknown" : Cut(name, 64)!;
    }

    // The phone's reply to a post: a "report your location" command when someone asked for one, else nothing.
    private object[] CommandsFor(string name) =>
        requests.Take(name) ? [new { _type = "cmd", action = "reportLocation" }] : [];

    private bool TokenMatches(string expected)
    {
        var given = Request.Query["key"].FirstOrDefault();
        var auth = Request.Headers.Authorization.FirstOrDefault();
        if (given == null && auth != null && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
                given = pair[(pair.IndexOf(':') + 1)..];
            }
            catch (FormatException) { }
        }
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given ?? ""), Encoding.UTF8.GetBytes(expected));
    }

    private static string? Str(JsonElement o, string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Num(JsonElement o, string key, out double value)
    {
        value = 0;
        return o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out value);
    }
    private static string? Cut(string? s, int max) => s is { Length: > 0 } ? (s.Length <= max ? s : s[..max]) : null;
}
