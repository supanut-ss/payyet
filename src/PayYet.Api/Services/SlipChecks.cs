using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PayYet.Config;

namespace PayYet.Services;

/// <summary>Cheap, offline sanity checks on an uploaded slip image.</summary>
public static class SlipInspector
{
    /// <summary>Pixel size of a JPEG or PNG without decoding it. Null when the headers cannot be read.</summary>
    public static (int Width, int Height)? TryGetSize(byte[] d)
    {
        if (d.Length > 24 && d[0] == 0x89 && d[1] == 0x50)   // PNG: IHDR width/height are big-endian at offset 16
            return (ReadInt(d, 16), ReadInt(d, 20));

        if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8)     // JPEG: walk the markers until a start-of-frame
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                var marker = d[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                var len = (d[i + 2] << 8) | d[i + 3];
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                i += 2 + len;
            }
        }
        return null;
    }

    private static int ReadInt(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
}

/// <param name="Checked">False when no verifier is configured or it could not be reached: the slip is then only checked by the collector.</param>
/// <param name="Ok">Meaningful only when Checked: the bank confirmed the slip and the amount matches.</param>
public record SlipVerification(bool Checked, bool Ok, string? Ref = null, string? Error = null)
{
    public static readonly SlipVerification Skipped = new(false, false);
}

/// <summary>
/// Optional bank-side slip verification through SlipOK (https://slipok.com). Off unless <c>SlipVerify</c> is configured:
/// the slip is sent to the provider, which reads its QR code and confirms amount and transaction reference with the bank.
/// Network or provider outages never block a payer: they just fall back to the collector's manual check.
/// </summary>
public class SlipVerifier(HttpClient http, IOptions<AppSettings> options, ILogger<SlipVerifier> log)
{
    private SlipVerifySection Cfg => options.Value.SlipVerify;
    public bool Enabled => Cfg.Provider.Equals("slipok", StringComparison.OrdinalIgnoreCase) && Cfg.BranchId != "" && Cfg.ApiKey != "";

    public async Task<SlipVerification> VerifyAsync(byte[] image, string contentType, long expectedSatang)
    {
        if (!Enabled) return SlipVerification.Skipped;
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "files", "slip" + (contentType == "image/png" ? ".png" : ".jpg"));
            form.Add(new StringContent((expectedSatang / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), "amount");
            form.Add(new StringContent("true"), "log");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.slipok.com/api/line/apikey/{Cfg.BranchId}") { Content = form };
            req.Headers.Add("x-authorization", Cfg.ApiKey);
            using var res = await http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // a provider-side failure (quota, auth, outage) must not look like a bad slip
            if ((int)res.StatusCode >= 500 || (int)res.StatusCode is 401 or 403 or 429)
            {
                log.LogWarning("Slip verifier unavailable: HTTP {Status}", (int)res.StatusCode);
                return SlipVerification.Skipped;
            }

            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("data", out var data))
            {
                var reference = data.TryGetProperty("transRef", out var tr) ? tr.GetString() : null;
                if (data.TryGetProperty("amount", out var amt) && amt.TryGetDecimal(out var baht)
                    && Math.Abs(baht * 100m - expectedSatang) > 0.5m)
                    return new SlipVerification(true, false, reference, $"ยอดในสลิป ฿{baht:0.##} ไม่ตรงกับยอดที่แจ้ง ฿{expectedSatang / 100m:0.##}");
                return new SlipVerification(true, true, reference);
            }

            var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            return new SlipVerification(true, false, null, code == 1012 ? "สลิปนี้เคยถูกใช้แล้ว" : $"ตรวจสลิปไม่ผ่าน: {message ?? "อ่านข้อมูลสลิปไม่ได้"}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(e, "Slip verifier failed; falling back to manual check");
            return SlipVerification.Skipped;
        }
    }
}
