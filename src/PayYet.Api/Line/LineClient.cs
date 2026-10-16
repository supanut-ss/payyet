using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PayYet.Config;

namespace PayYet.Line;

public record LineProfile(string UserId, string? DisplayName, string? PictureUrl = null);

/// <summary>Thin wrapper over the LINE Messaging API (no official .NET SDK).</summary>
public class LineClient(HttpClient http, IOptions<AppSettings> options, ILogger<LineClient> log)
{
    private const string Api = "https://api.line.me/v2/bot";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private LineSection Cfg => options.Value.Line;

    public Task<bool> ReplyAsync(string replyToken, params object[] messages) =>
        PostAsync($"{Api}/message/reply", new { replyToken, messages });

    public Task<bool> PushAsync(string to, params object[] messages) =>
        PostAsync($"{Api}/message/push", new { to, messages });

    public async Task<LineProfile?> GetGroupMemberProfileAsync(string groupId, string userId) =>
        await FetchProfileAsync($"{Api}/group/{groupId}/member/{userId}");

    public async Task<LineProfile?> GetProfileAsync(string userId) =>
        await FetchProfileAsync($"{Api}/profile/{userId}");

    private async Task<LineProfile?> FetchProfileAsync(string url)
    {
        var doc = await GetJsonAsync(url);
        if (doc == null) return null;
        var root = doc.RootElement;
        return new LineProfile(root.GetProperty("userId").GetString()!,
            root.TryGetProperty("displayName", out var n) ? n.GetString() : null,
            root.TryGetProperty("pictureUrl", out var pic) ? pic.GetString() : null);
    }

    /// <summary>Group name and picture (works for every account). Null when the call failed.</summary>
    public async Task<(string? Name, string? PictureUrl)?> GetGroupSummaryAsync(string groupId)
    {
        var doc = await GetJsonAsync($"{Api}/group/{groupId}/summary");
        if (doc == null) return null;
        var r = doc.RootElement;
        return (r.TryGetProperty("groupName", out var n) ? n.GetString() : null,
                r.TryGetProperty("pictureUrl", out var p) ? p.GetString() : null);
    }

    public async Task<int?> GetGroupMemberCountAsync(string groupId)
    {
        var doc = await GetJsonAsync($"{Api}/group/{groupId}/members/count");
        return doc != null && doc.RootElement.TryGetProperty("count", out var n) ? n.GetInt32() : null;
    }

    /// <summary>Verified / premium accounts only.</summary>
    public async Task<List<string>> GetGroupMemberIdsAsync(string groupId)
    {
        var ids = new List<string>();
        string? start = null;
        do
        {
            var doc = await GetJsonAsync($"{Api}/group/{groupId}/members/ids" + (start == null ? "" : $"?start={start}"));
            if (doc == null) break;
            foreach (var id in doc.RootElement.GetProperty("memberIds").EnumerateArray()) ids.Add(id.GetString()!);
            start = doc.RootElement.TryGetProperty("next", out var next) ? next.GetString() : null;
        } while (start != null);
        return ids;
    }

    /// <summary>Verifies a LIFF ID token with LINE and returns the LINE user id (sub) and display name.</summary>
    public async Task<(string UserId, string? Name)?> VerifyIdTokenAsync(string idToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id_token"] = idToken,
            ["client_id"] = Cfg.LoginChannelId
        });
        try
        {
            using var res = await http.PostAsync("https://api.line.me/oauth2/v2.1/verify", content);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("ID token verify failed: {Status}", (int)res.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var sub = doc.RootElement.GetProperty("sub").GetString();
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            return sub == null ? null : (sub, name);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ID token verify error");
            return null;
        }
    }

    private async Task<bool> PostAsync(string url, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Cfg.ChannelAccessToken);
        try
        {
            using var res = await http.SendAsync(req);
            if (res.IsSuccessStatusCode) return true;
            log.LogWarning("LINE {Url} -> {Status}: {Body}", url, (int)res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            log.LogError(ex, "LINE call failed: {Url}", url);
        }
        return false;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Cfg.ChannelAccessToken);
        try
        {
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                log.LogInformation("LINE GET {Url} -> {Status}", url, (int)res.StatusCode);
                return null;
            }
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            log.LogError(ex, "LINE GET failed: {Url}", url);
            return null;
        }
    }
}

public static class LineSignature
{
    public static bool IsValid(byte[] body, string? header, string channelSecret)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(channelSecret)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(body));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header));
    }
}
