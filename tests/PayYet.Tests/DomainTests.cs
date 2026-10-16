using System.Security.Cryptography;
using System.Text;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;
using PayYet.Services;

namespace PayYet.Tests;

public class SplitCalculatorTests
{
    [Theory]
    [InlineData(1000, 3)]
    [InlineData(10000, 4)]
    [InlineData(1, 5)]
    public void Equal_sum_is_exact_and_shares_differ_by_at_most_one_satang(long total, int n)
    {
        var shares = SplitCalculator.Equal(total, n);
        Assert.Equal(total, shares.Sum());
        Assert.True(shares.Max() - shares.Min() <= 1);
    }

    [Fact]
    public void Equal_gives_remainder_to_first_people()
    {
        Assert.Equal(new long[] { 334, 333, 333 }, SplitCalculator.Equal(1000, 3));
    }
}

public class PromptPayTests
{
    [Fact]
    public void Crc16_matches_ccitt_false_check_value()
    {
        Assert.Equal((ushort)0x29B1, PromptPay.Crc16("123456789"));
    }

    [Fact]
    public void Phone_payload_has_expected_structure_and_valid_crc()
    {
        var p = PromptPay.Payload("081-234-5678", 10000);
        Assert.StartsWith("000201010212", p);                       // dynamic QR
        Assert.Contains("0016A000000677010111011300668123456785303764", p);   // PromptPay AID + 0066 phone + THB
        Assert.Contains("5406100.00", p);                            // tag 54, length 06, "100.00"
        Assert.EndsWith("5802TH" + "6304" + PromptPay.Crc16(p[..^4]).ToString("X4"), p);
    }

    [Fact]
    public void National_id_uses_tag_02()
    {
        var p = PromptPay.Payload("1234567890123", null);
        Assert.Contains("02131234567890123", p);
        Assert.StartsWith("000201010211", p);                       // static QR (no amount)
        Assert.DoesNotContain("54", p[12..20]);
    }

    [Fact]
    public void Invalid_id_throws()
    {
        Assert.Throws<ArgumentException>(() => PromptPay.Payload("12345", 100));
        Assert.False(PromptPay.IsValidId("12345"));
        Assert.True(PromptPay.IsValidId("0812345678"));
    }
}

public class QuickEntryParserTests
{
    [Fact]
    public void Not_a_command_returns_null()
    {
        Assert.Null(QuickEntryParser.Parse("สวัสดี"));
        Assert.Null(QuickEntryParser.Parse("ยอด"));
    }

    [Fact]
    public void Bare_command_is_empty()
    {
        Assert.True(QuickEntryParser.Parse("เก็บเงิน")!.IsEmpty);
    }

    [Fact]
    public void Amount_and_title()
    {
        var p = QuickEntryParser.Parse("เก็บ 500 ค่าข้าว")!;
        Assert.True(p.PerPerson);   // เก็บ = the amount is for one person
        Assert.Equal(TokenKind.Number, p.Tokens[0].Kind);
        Assert.Equal(500m, p.Tokens[0].Value);
        Assert.Equal("ค่าข้าว", p.Tokens[1].Text);
    }

    [Fact]
    public void Per_person_keyword_in_any_position()
    {
        Assert.True(QuickEntryParser.Parse("เก็บ 100 คนละ")!.PerPerson);
        Assert.True(QuickEntryParser.Parse("เก็บ 100คนละ")!.PerPerson);
    }

    [Fact]
    public void Thai_digits_commas_and_currency_words_are_normalised()
    {
        var p = QuickEntryParser.Parse("เก็บ ๑,๕๐๐ บาท")!;
        Assert.Single(p.Tokens);
        Assert.Equal(1500m, p.Tokens[0].Value);
        Assert.Equal(1250.50m, QuickEntryParser.Parse("เก็บ ฿1,250.50")!.Tokens[0].Value);
    }

    [Fact]
    public void Exclusions_collect_all_following_words()
    {
        var p = QuickEntryParser.Parse("เก็บ 500 ยกเว้น สมชาย มาลี")!;
        Assert.Equal(new[] { "สมชาย", "มาลี" }, p.ExcludedWords);
        Assert.Single(p.Tokens);
    }

    [Fact]
    public void Name_amount_pairs_stay_in_order()
    {
        var p = QuickEntryParser.Parse("เก็บ สมชาย 200 มาลี 300")!;
        Assert.Equal(new[] { "สมชาย", "200", "มาลี", "300" }, p.Tokens.Select(t => t.Text));
    }
}

public class LineSignatureTests
{
    [Fact]
    public void Accepts_valid_and_rejects_tampered_body()
    {
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");
        const string secret = "channel-secret";
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToBase64String(h.ComputeHash(body));

        Assert.True(LineSignature.IsValid(body, sig, secret));
        Assert.False(LineSignature.IsValid(Encoding.UTF8.GetBytes("{}"), sig, secret));
        Assert.False(LineSignature.IsValid(body, null, secret));
        Assert.False(LineSignature.IsValid(body, sig, ""));
    }
}

public class MatchPeopleTests
{
    private static AppUser U(string name) => new() { DisplayName = name };

    [Fact]
    public void Exact_match_wins_over_partial()
    {
        var pool = new[] { U("สมชาย"), U("สมชายใจดี") };
        var m = DraftService.MatchPeople("สมชาย", pool);
        Assert.Single(m);
        Assert.Equal("สมชาย", m[0].DisplayName);
    }

    [Fact]
    public void Partial_match_can_be_ambiguous_and_single_chars_never_match()
    {
        var pool = new[] { U("สมชายใจดี"), U("สมชายรักเรียน") };
        Assert.Equal(2, DraftService.MatchPeople("สมชาย", pool).Count);
        Assert.Empty(DraftService.MatchPeople("ส", pool));
    }
}
