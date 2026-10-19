using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Domain;
using PayYet.Services;
using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PayYet.Tests;

public class SlipQrTests
{
    /// <summary>A slip-style payload: tag 00 {00 api id, 01 bank, 02 reference}, 51 country, 91 CRC.</summary>
    private static string Payload(string bank = "014", string reference = "202610170812345678ABC", bool badCrc = false)
    {
        string T(string tag, string v) => tag + v.Length.ToString("00") + v;
        var container = T("00", "000001") + T("01", bank) + T("02", reference);
        var body = T("00", container) + T("51", "TH") + "9104";
        var crc = PromptPay.Crc16(body).ToString("X4");
        return body + (badCrc ? (crc == "0000" ? "FFFF" : "0000") : crc);
    }

    private static byte[] QrPng(string text) => new PngByteQRCode(new QRCodeGenerator().CreateQrCode(text, QRCodeGenerator.ECCLevel.M)).GetGraphic(10);

    private static SlipQrVerifier Verifier(bool requireQr = false, int maxAgeDays = 7) =>
        new(Options.Create(new AppSettings { SlipVerify = { QrCheck = true, RequireQr = requireQr, MaxAgeDays = maxAgeDays } }));

    private static string RefFor(DateOnly day) => day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + "123456ABCD";

    [Fact]
    public void A_valid_slip_qr_gives_bank_and_reference()
    {
        var (kind, slip, error) = SlipQrParser.Parse(Payload());
        Assert.Equal(SlipQrParser.Kind.Ok, kind);
        Assert.Null(error);
        Assert.Equal("014", slip!.SendingBank);
        Assert.Equal("014:202610170812345678ABC", slip.Key);
    }

    [Fact]
    public void References_of_every_bank_style_are_accepted()
    {
        foreach (var (bank, reference) in new[] { ("004", "016283130823APP11348"), ("014", "202609290V0OuizguHFJfpegq"), ("002", "2026092114082224001796308"),
                                                  ("006", "Ab181ba0729ae4a22"), ("024", "2607245935201926-TFR") })
            Assert.Equal(SlipQrParser.Kind.Ok, SlipQrParser.Parse(Payload(bank, reference)).Kind);
    }

    [Fact]
    public void Tampered_unknown_bank_and_payment_qrs_are_told_apart()
    {
        Assert.Equal(SlipQrParser.Kind.Invalid, SlipQrParser.Parse(Payload(badCrc: true)).Kind);          // CRC does not match
        Assert.Equal(SlipQrParser.Kind.Invalid, SlipQrParser.Parse(Payload(bank: "999")).Kind);           // no such bank (CRC is valid)
        Assert.Equal(SlipQrParser.Kind.NotASlipQr, SlipQrParser.Parse(PromptPay.Payload("0812345678", 10000)).Kind);   // a payment request QR
        Assert.Equal(SlipQrParser.Kind.NotASlipQr, SlipQrParser.Parse("https://example.com/not-a-slip-at-all-just-a-link").Kind);
    }

    [Fact]
    public void The_reference_date_is_read_only_when_it_looks_like_a_date()
    {
        Assert.Equal(new DateOnly(2026, 10, 17), SlipQrParser.DateFromReference("20261017081234ABC"));
        Assert.Null(SlipQrParser.DateFromReference("ABC123456789"));
        Assert.Null(SlipQrParser.DateFromReference("99991399ZZZ"));
    }

    [Fact]
    public void A_slip_image_is_decoded_and_its_reference_is_returned_for_duplicate_blocking()
    {
        var today = BangkokTime.Today;
        var png = QrPng(Payload(reference: RefFor(today)));

        var result = Verifier().Verify(png, DateTime.UtcNow.AddHours(-1));

        Assert.True(result.Checked);
        Assert.True(result.Ok);
        Assert.Equal("QR", result.Source);
        Assert.Equal("014:" + RefFor(today), result.Ref);
    }

    [Fact]
    public void Old_or_pre_bill_slips_and_broken_qrs_are_refused()
    {
        var today = BangkokTime.Today;
        var billCreated = DateTime.UtcNow.AddHours(-1);

        var old = Verifier().Verify(QrPng(Payload(reference: RefFor(today.AddDays(-30)))), DateTime.UtcNow.AddDays(-60));
        Assert.False(old.Ok);
        Assert.Contains("เก่าเกิน", old.Error);

        var beforeBill = Verifier().Verify(QrPng(Payload(reference: RefFor(today.AddDays(-2)))), billCreated);
        Assert.False(beforeBill.Ok);
        Assert.Contains("ก่อนสร้างบิล", beforeBill.Error);

        var forged = Verifier().Verify(QrPng(Payload(reference: RefFor(today), badCrc: true)), billCreated);
        Assert.True(forged.Checked);
        Assert.False(forged.Ok);
    }

    [Fact]
    public void A_slip_without_a_qr_falls_back_to_manual_check_unless_a_qr_is_required()
    {
        using var blank = new Image<Rgba32>(400, 600, Color.White);
        using var ms = new MemoryStream();
        blank.SaveAsPng(ms);

        var lenient = Verifier().Verify(ms.ToArray(), DateTime.UtcNow);
        Assert.False(lenient.Checked);                        // not blocked: the collector checks it
        Assert.Contains("ไม่พบ QR", lenient.Note);

        var strict = Verifier(requireQr: true).Verify(ms.ToArray(), DateTime.UtcNow);
        Assert.True(strict.Checked);
        Assert.False(strict.Ok);
    }

    [Fact]
    public void Amounts_are_extracted_from_ocr_text_and_compared_with_the_declared_amount()
    {
        var text = string.Join("\n", "โอนเงินสำเร็จ", "จำนวน: 1,500.00 บาท", "ค่าธรรมเนียม: 0.00 บาท", "เลขที่รายการ: 016283130823APP11348", "ยอด 91.54 THB");
        var found = SlipAmountChecker.ExtractAmounts(text);
        Assert.Equal(new[] { 1500m, 91.54m }, found);                                  // the fee (0.00) and the reference are ignored

        Assert.Equal(AmountCheck.Match, SlipAmountChecker.Compare(found, 150000));
        Assert.Equal(AmountCheck.Match, SlipAmountChecker.Compare(found, 9154));
        Assert.Equal(AmountCheck.Mismatch, SlipAmountChecker.Compare(found, 100000));  // slip says 1,500, payer says 1,000
        Assert.Equal(AmountCheck.Unknown, SlipAmountChecker.Compare(SlipAmountChecker.ExtractAmounts("no numbers here"), 100000));
        Assert.Equal(new[] { 35000m }, SlipAmountChecker.ExtractAmounts("35,000.00"));
    }
}
