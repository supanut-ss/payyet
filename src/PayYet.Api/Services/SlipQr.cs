using System.Globalization;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Domain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;
using ZXing.Common;
using ZXing.ImageSharp;

namespace PayYet.Services;

/// <param name="SendingBank">3-digit bank code of the sender's bank (e.g. 014 = SCB).</param>
/// <param name="TransRef">Transaction reference printed by the sending bank.</param>
public record SlipQr(string SendingBank, string TransRef)
{
    /// <summary>Stored in payments.slip_ref (unique): one transfer can only ever pay one bill.</summary>
    public string Key => $"{SendingBank}:{TransRef}";
}

/// <summary>
/// Reads the "mini QR" printed on Thai bank-transfer slips. Layout (EMV-style TLV, tag + 2-digit length + value):
/// tag 00 holds sub-tags 00 = API id, 01 = sending bank, 02 = transaction reference; tag 51 = country; tag 91 = CRC-16 of everything before it.
/// This proves the QR is intact and unique - it does NOT prove the transfer happened: a forged slip with a correct CRC passes.
/// </summary>
public static class SlipQrParser
{
    /// <summary>Bank codes of Thai banks (BOT codes). A QR naming any other bank is rejected.</summary>
    public static readonly HashSet<string> Banks = new()
    {
        "002", "004", "006", "011", "014", "017", "018", "020", "022", "024", "025", "030", "031", "033", "034", "039", "045",
        "065", "066", "067", "069", "070", "071", "073", "074"
    };

    public enum Kind { NotASlipQr, Invalid, Ok }

    public static (Kind Kind, SlipQr? Slip, string? Error) Parse(string payload)
    {
        payload = payload.Trim();
        // a PromptPay payment QR starts 000201: it is a payment request, not a slip
        if (payload.Length < 30 || payload.StartsWith("000201", StringComparison.Ordinal)) return (Kind.NotASlipQr, null, null);

        var top = Tlv(payload);
        if (top == null || !top.TryGetValue("00", out var container)) return (Kind.NotASlipQr, null, null);
        var sub = Tlv(container);
        if (sub == null || !sub.TryGetValue("00", out var apiId) || !apiId.StartsWith("0000", StringComparison.Ordinal) || !sub.ContainsKey("01") || !sub.ContainsKey("02"))
            return (Kind.NotASlipQr, null, null);

        // CRC-16/CCITT-FALSE over the payload up to and including the "9104" prefix of the CRC tag, which must come last
        if (!top.ContainsKey("91") || payload.Length < 8 || payload[^8..^4] != "9104")
            return (Kind.Invalid, null, "QR ของสลิปไม่สมบูรณ์");
        var expected = PromptPay.Crc16(payload[..^4]).ToString("X4", CultureInfo.InvariantCulture);
        if (!expected.Equals(payload[^4..], StringComparison.OrdinalIgnoreCase))
            return (Kind.Invalid, null, "QR ของสลิปไม่ถูกต้อง (CRC ไม่ตรง) สลิปอาจถูกแก้ไข");

        var bank = sub["01"];
        var reference = sub["02"];
        if (!Banks.Contains(bank)) return (Kind.Invalid, null, $"ไม่รู้จักรหัสธนาคารในสลิป ({bank})");
        // references may contain '-' (UOB) besides letters and digits
        if (reference.Length < 8 || reference.Length > 40 || !reference.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return (Kind.Invalid, null, "เลขอ้างอิงในสลิปไม่ถูกต้อง");
        return (Kind.Ok, new SlipQr(bank, reference), null);
    }

    /// <summary>Date embedded at the start of the reference (yyyyMMdd) when it is plausible; many banks do this, some do not.</summary>
    public static DateOnly? DateFromReference(string transRef)
    {
        if (transRef.Length >= 8 && transRef[..8].All(char.IsDigit)
            && DateOnly.TryParseExact(transRef[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            && d.Year >= 2015 && d <= BangkokTime.Today.AddDays(2))
            return d;
        return null;
    }

    private static Dictionary<string, string>? Tlv(string s)
    {
        var result = new Dictionary<string, string>();
        var i = 0;
        while (i + 4 <= s.Length)
        {
            var tag = s.Substring(i, 2);
            if (!int.TryParse(s.AsSpan(i + 2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var len) || i + 4 + len > s.Length) return null;
            result[tag] = s.Substring(i + 4, len);
            i += 4 + len;
        }
        return i == s.Length ? result : null;
    }
}

/// <summary>Offline slip check: decode the QR, verify its CRC, bank code, age, and give back the reference to block re-use.</summary>
public class SlipQrVerifier(IOptions<AppSettings> options)
{
    private SlipVerifySection Cfg => options.Value.SlipVerify;

    /// <summary>All QR payloads found in the image (empty when none or the image cannot be decoded).</summary>
    public static IReadOnlyList<string> ReadQrPayloads(byte[] image)
    {
        try
        {
            var reader = new ZXing.ImageSharp.BarcodeReader<Rgba32>
            {
                AutoRotate = true,
                Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } }
            };
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(image);
            var found = reader.DecodeMultiple(img);
            return found?.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).ToList() ?? new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <param name="billCreatedUtc">A slip dated before the bill was created cannot be paying it.</param>
    public SlipVerification Verify(byte[] image, DateTime billCreatedUtc)
    {
        if (!Cfg.QrCheck) return SlipVerification.Skipped;

        SlipQr? slip = null;
        foreach (var payload in ReadQrPayloads(image))
        {
            var (kind, parsed, error) = SlipQrParser.Parse(payload);
            if (kind == SlipQrParser.Kind.Invalid) return new SlipVerification(true, false, null, error, "QR");
            if (kind == SlipQrParser.Kind.Ok) { slip = parsed; break; }
        }

        if (slip == null)
            return Cfg.RequireQr
                ? new SlipVerification(true, false, null, "ไม่พบ QR ของสลิป กรุณาแนบภาพสลิปเต็มใบจากแอปธนาคาร ให้เห็น QR ชัดเจน", "QR")
                : SlipVerification.Skipped with { Note = "ไม่พบ QR ในสลิป โปรดตรวจยอดและวันที่ในสลิปเอง" };

        if (SlipQrParser.DateFromReference(slip.TransRef) is { } day)
        {
            var created = DateOnly.FromDateTime(BangkokTime.FromUtc(billCreatedUtc));
            if (day < created) return new SlipVerification(true, false, null, "สลิปนี้มีวันที่ก่อนสร้างบิล ใช้จ่ายบิลนี้ไม่ได้", "QR");
            if (BangkokTime.Today.DayNumber - day.DayNumber > Cfg.MaxAgeDays)
                return new SlipVerification(true, false, null, $"สลิปเก่าเกิน {Cfg.MaxAgeDays} วัน", "QR");
        }

        return new SlipVerification(true, true, slip.Key, null, "QR", $"อ่าน QR ของสลิปได้ (ธนาคาร {slip.SendingBank}) ยังไม่ได้ตรวจกับธนาคาร");
    }
}
