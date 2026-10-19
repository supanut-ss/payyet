using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;

namespace PayYet.Controllers;

[ApiController]
public class OwnTracksController(IOptions<AppSettings> options, AppDbContext db) : ControllerBase
{
    // OwnTracks (HTTP mode) POSTs one JSON object per report, e.g.
    // {"_type":"location","tid":"ma","lat":13.75,"lon":100.5,"acc":12,"batt":87,"tst":1760000000}
    // Auth: the token as the HTTP Basic password, or as ?key= (OwnTracks lets the URL carry a query string).
    // The person is the X-Limit-U header (OwnTracks "username"), falling back to the tracker id.
    [HttpPost("owntracks")]
    public async Task<IActionResult> Receive([FromBody] JsonElement body)
    {
        var expected = options.Value.OwnTracks.Token;
        if (string.IsNullOrEmpty(expected)) return StatusCode(503, "OwnTracks:Token is not configured");
        if (!TokenMatches(expected)) return Unauthorized();

        // OwnTracks expects 200 + a JSON array (commands/cards for the phone); other message types are acknowledged and ignored.
        if (body.ValueKind != JsonValueKind.Object || Str(body, "_type") != "location"
            || !Num(body, "lat", out var lat) || !Num(body, "lon", out var lon)
            || lat is < -90 or > 90 || lon is < -180 or > 180)
            return Ok(Array.Empty<object>());

        var name = (Request.Headers["X-Limit-U"].FirstOrDefault() ?? Str(body, "tid") ?? "").Trim();
        if (name.Length == 0) name = "unknown";
        var now = DateTime.UtcNow;
        var recorded = Num(body, "tst", out var tst) && tst > 0 && tst < 4_102_444_800 ? DateTimeOffset.FromUnixTimeSeconds((long)tst).UtcDateTime : now;

        db.LocationPings.Add(new LocationPing
        {
            Name = Cut(name, 64),
            Device = Cut(Request.Headers["X-Limit-D"].FirstOrDefault(), 64),
            Lat = lat,
            Lon = lon,
            Accuracy = Num(body, "acc", out var acc) ? (int)acc : null,
            Battery = Num(body, "batt", out var batt) ? (int)batt : null,
            RecordedAt = recorded,
            ReceivedAt = now,
        });
        await db.SaveChangesAsync();
        return Ok(Array.Empty<object>());
    }

    // Latest position of each person (or just ?name=). Same token as the POST.
    [HttpGet("owntracks/latest")]
    public async Task<IActionResult> Latest([FromQuery] string? name)
    {
        var expected = options.Value.OwnTracks.Token;
        if (string.IsNullOrEmpty(expected)) return StatusCode(503, "OwnTracks:Token is not configured");
        if (!TokenMatches(expected)) return Unauthorized();

        var latest = db.LocationPings.Where(p => p.Id == db.LocationPings
            .Where(q => q.Name == p.Name).OrderByDescending(q => q.RecordedAt).ThenByDescending(q => q.Id).Select(q => q.Id).First());
        if (!string.IsNullOrWhiteSpace(name)) { var n = name.Trim(); latest = latest.Where(p => p.Name == n); }

        var rows = await latest.OrderBy(p => p.Name).ToListAsync();
        return Ok(rows.Select(p => new
        {
            name = p.Name,
            device = p.Device,
            lat = p.Lat,
            lon = p.Lon,
            accuracy = p.Accuracy,
            battery = p.Battery,
            recordedAt = DateTime.SpecifyKind(p.RecordedAt, DateTimeKind.Utc),
        }));
    }

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
