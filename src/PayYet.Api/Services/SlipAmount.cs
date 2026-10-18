using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PayYet.Config;
using Tesseract;

namespace PayYet.Services;

public enum AmountCheck { Unknown, Match, Mismatch }

/// <param name="Found">Every money amount (with 2 decimals, not zero) read from the slip, in baht.</param>
public record SlipAmountResult(AmountCheck Check, IReadOnlyList<decimal> Found);

/// <summary>
/// Reads the amount printed on a slip with OCR (Tesseract, English data: only the digits are needed) and compares it with
/// the amount the payer declared. The slip QR carries no amount, so this is the offline way to catch "slip shows 100, payer says 1000".
/// OCR can fail (blurry image, native library missing on the host): then the result is Unknown and the collector checks by hand.
/// </summary>
public class SlipAmountChecker(IOptions<AppSettings> options, IWebHostEnvironment env, ILogger<SlipAmountChecker> log)
{
    private static readonly Regex Money = new(@"(?<![\d.,])\d{1,3}(?:,\d{3})+\.\d{2}(?!\d)|(?<![\d.,])\d+\.\d{2}(?!\d)", RegexOptions.Compiled);

    private readonly object _lock = new();
    private TesseractEngine? _engine;
    private bool _unavailable;

    public bool Enabled => options.Value.SlipVerify.AmountCheck != "off" && !_unavailable;

    /// <summary>All "1,234.56"-style amounts in the text, zero excluded (fees are printed as 0.00).</summary>
    public static IReadOnlyList<decimal> ExtractAmounts(string text) =>
        Money.Matches(text)
            .Select(m => decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m)
            .Where(v => v > 0).Distinct().ToList();

    public static AmountCheck Compare(IReadOnlyList<decimal> found, long declaredSatang)
    {
        if (found.Count == 0) return AmountCheck.Unknown;
        return found.Any(v => Math.Round(v * 100m) == declaredSatang) ? AmountCheck.Match : AmountCheck.Mismatch;
    }

    /// <summary>True when Tesseract and its data load on this host (reported by /health so a deploy shows it).</summary>
    public bool SelfTest()
    {
        if (!Enabled) return false;
        try
        {
            lock (_lock) { _engine ??= new TesseractEngine(Path.Combine(AppContext.BaseDirectory, "tessdata"), "eng", EngineMode.Default); }
            return true;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Slip amount OCR self-test failed");
            _unavailable = true;
            return false;
        }
    }

    public SlipAmountResult Check(byte[] image, long declaredSatang)
    {
        var found = Read(image);
        return new SlipAmountResult(found == null ? AmountCheck.Unknown : Compare(found, declaredSatang), found ?? new List<decimal>());
    }

    private IReadOnlyList<decimal>? Read(byte[] image)
    {
        if (!Enabled) return null;
        try
        {
            lock (_lock)   // one engine, one image at a time (slips are rare and a read takes about half a second)
            {
                _engine ??= new TesseractEngine(Path.Combine(AppContext.BaseDirectory, "tessdata"), "eng", EngineMode.Default);
                using var pix = Pix.LoadFromMemory(image);
                using var page = _engine.Process(pix);
                return ExtractAmounts(page.GetText());
            }
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or TesseractException or TypeInitializationException or IOException or InvalidOperationException)
        {
            // the host cannot run Tesseract (missing VC++ runtime / data): stop trying and let the collector check by hand
            log.LogWarning(e, "Slip amount OCR is unavailable; falling back to manual check");
            _unavailable = true;
            return null;
        }
    }
}
