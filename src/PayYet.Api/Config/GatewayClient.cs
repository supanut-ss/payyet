using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PayYet.Config;

/// <summary>Settings for fetching credentials from the D2D gateway (empty BaseUrl = off, local appsettings are used).</summary>
public class GatewaySection
{
    public string BaseUrl { get; set; } = "";             // https://gateway.drivetodev.online
    public string AppId { get; set; } = "";               // X-App-Id
    public string ApiKey { get; set; } = "";              // X-Api-Key
    public string MessagingChannelId { get; set; } = "";  // optional: empty = the app's own Messaging channel at the gateway
    public bool Enabled => BaseUrl.Length > 0 && AppId.Length > 0 && ApiKey.Length > 0;
}

public record GatewayDb([property: JsonPropertyName("connectionString")] string ConnectionString);

public record GatewayLine(
    [property: JsonPropertyName("channelSecret")] string ChannelSecret,
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("accessTokenExpiresAt")] DateTimeOffset? AccessTokenExpiresAt);

/// <summary>Reads this app's database connection string and LINE keys from the gateway (kept in memory only).</summary>
public class GatewayClient(HttpClient http, GatewaySection cfg)
{
    public GatewayClient(GatewaySection cfg) : this(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, cfg) { }

    public async Task<GatewayDb> GetDatabaseAsync() => await GetAsync<GatewayDb>("api/v1/database/credentials");

    public async Task<GatewayLine> GetLineAsync() =>
        await GetAsync<GatewayLine>(cfg.MessagingChannelId.Length > 0 ? $"api/v1/line/channels/{Uri.EscapeDataString(cfg.MessagingChannelId)}/credentials" : "api/v1/line/credentials?type=Messaging");

    private async Task<T> GetAsync<T>(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, cfg.BaseUrl.TrimEnd('/') + "/" + path);
        req.Headers.Add("X-App-Id", cfg.AppId);
        req.Headers.Add("X-Api-Key", cfg.ApiKey);
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Gateway {path} answered {(int)res.StatusCode}");
        return await res.Content.ReadFromJsonAsync<T>() ?? throw new HttpRequestException($"Gateway {path} returned an empty body");
    }
}

public interface ILineTokenSource
{
    Task<string> GetAsync();
}

/// <summary>Keeps the gateway's access token in memory and fetches a new one a minute before it expires.</summary>
public class GatewayLineTokenSource(GatewayClient gateway, string initialToken, DateTimeOffset? initialExpiry) : ILineTokenSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _token = initialToken;
    private DateTimeOffset? _expiresAt = initialExpiry;

    public async Task<string> GetAsync()
    {
        if (!Stale()) return _token;
        await _gate.WaitAsync();
        try
        {
            if (Stale())
            {
                var l = await gateway.GetLineAsync();
                _token = l.AccessToken ?? "";
                _expiresAt = l.AccessTokenExpiresAt;
            }
            return _token;
        }
        finally { _gate.Release(); }
    }

    private bool Stale() => _token.Length == 0 || (_expiresAt != null && _expiresAt.Value - TimeSpan.FromMinutes(1) <= DateTimeOffset.UtcNow);
}
