using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PayYet.Domain;

public static class BangkokTime
{
    private static readonly TimeZoneInfo Tz = Find();

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Asia/Bangkok", "SE Asia Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    }

    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);
    public static DateOnly Today => DateOnly.FromDateTime(Now);
    /// <summary>Midnight (Bangkok) at the start of the given day, as UTC.</summary>
    public static DateTime StartOfDayUtc(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Tz);
    public static DateTime FromUtc(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);
}

public static class Money
{
    /// <summary>500 → "500", 9950 satang → "99.50"; thousands separated.</summary>
    public static string Format(long satang)
    {
        var baht = satang / 100m;
        return satang % 100 == 0
            ? baht.ToString("N0", CultureInfo.InvariantCulture)
            : baht.ToString("N2", CultureInfo.InvariantCulture);
    }

    public static long ToSatang(decimal baht) => (long)Math.Round(baht * 100m, MidpointRounding.AwayFromZero);
}

public static class Token
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static string New(int length = 12)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }

    public static string Hex(int bytes = 24) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}

public static class SplitCalculator
{
    /// <summary>Splits total across n people; the remainder satang go to the first people so the sum is exact.</summary>
    public static long[] Equal(long totalSatang, int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        var baseShare = totalSatang / n;
        var rest = (int)(totalSatang % n);
        var shares = new long[n];
        for (var i = 0; i < n; i++) shares[i] = baseShare + (i < rest ? 1 : 0);
        return shares;
    }
}

public static class PromptPay
{
    /// <summary>EMVCo PromptPay payload (Thai QR). id = phone (10 digits), national/tax ID (13) or e-wallet (15).</summary>
    public static string Payload(string id, long? amountSatang)
    {
        id = new string(id.Where(char.IsDigit).ToArray());
        string subTag, target;
        if (id.Length == 10 && id[0] == '0') { subTag = "01"; target = "0066" + id[1..]; }
        else if (id.Length == 13) { subTag = "02"; target = id; }
        else if (id.Length == 15) { subTag = "03"; target = id; }
        else throw new ArgumentException("PromptPay ID must be a 10-digit phone, 13-digit ID or 15-digit e-wallet number.", nameof(id));

        var merchant = Tlv("00", "A000000677010111") + Tlv(subTag, target);
        var sb = new StringBuilder();
        sb.Append(Tlv("00", "01"));
        sb.Append(Tlv("01", amountSatang.HasValue ? "12" : "11"));
        sb.Append(Tlv("29", merchant));
        sb.Append(Tlv("53", "764"));
        if (amountSatang.HasValue)
            sb.Append(Tlv("54", (amountSatang.Value / 100m).ToString("0.00", CultureInfo.InvariantCulture)));
        sb.Append(Tlv("58", "TH"));
        sb.Append("6304");
        var body = sb.ToString();
        return body + Crc16(body).ToString("X4");
    }

    public static bool IsValidId(string id)
    {
        var digits = new string(id.Where(char.IsDigit).ToArray());
        return (digits.Length == 10 && digits[0] == '0') || digits.Length == 13 || digits.Length == 15;
    }

    private static string Tlv(string tag, string value) => tag + value.Length.ToString("00") + value;

    /// <summary>CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF).</summary>
    internal static ushort Crc16(string s)
    {
        ushort crc = 0xFFFF;
        foreach (var b in Encoding.ASCII.GetBytes(s))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
