using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PayYet.Domain;

public enum TokenKind { Number, Word }

public record EntryToken(TokenKind Kind, string Text, decimal Value = 0);

/// <summary>
/// Result of reading one "เก็บ ..." / "หาร ..." line. Names/titles are resolved later against real people.
/// <c>เก็บ</c> = the amount is for ONE person (each selected person pays it); <c>หาร</c> = the amount is a total split among the selected people.
/// </summary>
public record ParsedEntry(List<EntryToken> Tokens, bool PerPerson, List<string> ExcludedWords, bool Everyone = false)
{
    public bool IsEmpty => Tokens.Count == 0;
}

public static class QuickEntryParser
{
    private static readonly Regex Command = new(@"^\s*(?<verb>เก็บเงิน|เก็บ|หารเท่า|หาร)\s*(?<rest>.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly string[] EveryoneKeywords = { "ทุกคน", "ทั้งกลุ่ม", "ทั้งหมด" };
    private static readonly Regex NumberToken = new(@"^\d+(?:\.\d{1,2})?$", RegexOptions.Compiled);
    private static readonly Regex NumberPerPerson = new(@"^(?<n>\d+(?:\.\d{1,2})?)คนละ$", RegexOptions.Compiled);
    private static readonly string[] IgnoredKeywords = { "รวม" };

    /// <summary>Returns null when the text is not a collect command at all.</summary>
    public static ParsedEntry? Parse(string text)
    {
        var m = Command.Match(text);
        if (!m.Success) return null;

        var rest = Normalize(m.Groups["rest"].Value);
        var tokens = new List<EntryToken>();
        var excluded = new List<string>();
        var perPerson = !m.Groups["verb"].Value.StartsWith("หาร", StringComparison.Ordinal);   // เก็บ = per person, หาร = split a total
        var everyone = false;
        var excludeMode = false;

        var words = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < words.Length; i++)
        {
            var raw = words[i];
            if (excludeMode) { excluded.Add(raw); continue; }
            if (raw == "ยกเว้น") { excludeMode = true; continue; }
            if (raw == "คนละ") { perPerson = true; continue; }
            if (raw is "หาร" or "หารเท่า") { perPerson = false; continue; }
            if (Array.IndexOf(EveryoneKeywords, raw) >= 0) { everyone = true; continue; }
            if (Array.IndexOf(IgnoredKeywords, raw) >= 0) continue;

            var pp = NumberPerPerson.Match(raw);
            if (pp.Success)
            {
                perPerson = true;
                tokens.Add(new EntryToken(TokenKind.Number, pp.Groups["n"].Value, decimal.Parse(pp.Groups["n"].Value, CultureInfo.InvariantCulture)));
            }
            else if (NumberToken.IsMatch(raw))
            {
                tokens.Add(new EntryToken(TokenKind.Number, raw, decimal.Parse(raw, CultureInfo.InvariantCulture)));
            }
            else
            {
                tokens.Add(new EntryToken(TokenKind.Word, raw));
            }
        }

        return new ParsedEntry(tokens, perPerson, excluded, everyone);
    }

    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch is >= '๐' and <= '๙' ? (char)('0' + (ch - '๐')) : ch);   // Thai digits → ASCII

        var t = sb.ToString();
        t = Regex.Replace(t, @"(?<=\d),(?=\d{3})", "");   // 1,500 → 1500
        t = t.Replace("฿", " ").Replace("บาท", " ");
        return t.Trim();
    }
}
