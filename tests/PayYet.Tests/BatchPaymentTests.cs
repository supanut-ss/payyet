using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PayYet.Tests;

/// <summary>One slip paying several bills of the same collector.</summary>
public class BatchPaymentTests : DbTestBase
{
    private sealed class TempEnv : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "payyet-test-" + Guid.NewGuid().ToString("N"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private readonly AppUser _collector, _other, _me;
    private readonly BillService _bills;
    private readonly PaymentService _payments;

    public BatchPaymentTests()
    {
        _collector = AddUser("ผู้เก็บ", promptPay: "0812345678");
        _other = AddUser("ผู้เก็บอีกคน", promptPay: "0899999999");
        _me = AddUser("ผู้จ่าย");
        _bills = new BillService(Db);
        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            App = { PublicBaseUrl = "https://example.test" },
            Liff = { Id = "liff-test" },
            SlipVerify = { AmountCheck = "off" }
        });
        var env = new TempEnv();
        _payments = new PaymentService(Db, NewLine(), _bills, settings, env,
            new SlipVerifier(new HttpClient(Handler), settings, NullLogger<SlipVerifier>.Instance),
            new SlipQrVerifier(settings),
            new SlipAmountChecker(settings, env, NullLogger<SlipAmountChecker>.Instance));
    }

    private static byte[] SlipImage(int shade)
    {
        using var img = new Image<Rgba32>(400, 600, new Rgba32((byte)shade, 200, 200));   // different bytes → a different hash per call
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private async Task<string> Bill(AppUser collector, string title, long satang)
    {
        var r = await _bills.AddChargeAsync(collector.Id, null, _me.Id, title, SplitMode.EQUAL, new[] { (_me.Id, satang) });
        return r.Bill.Token;
    }

    [Fact]
    public async Task One_slip_pays_several_bills_and_one_approval_settles_all_of_them()
    {
        var t1 = await Bill(_collector, "ค่าข้าว", 10000);
        var t2 = await Bill(_collector, "ค่าเบียร์", 25050);

        var (ok, msg) = await _payments.SubmitSlipBatchAsync(_me.LineUserId, new[] { t1, t2 }, 35050, SlipImage(10));
        Assert.True(ok, msg);

        Assert.Equal(1, Handler.Pushes(_collector.LineUserId));                       // one card for the collector, not one per bill
        var all = await Db.Payments.ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all.Select(p => p.BatchId).Distinct());
        Assert.Equal(35050, all.Sum(p => p.AmountSatang));
        Assert.All(await Db.BillItems.ToListAsync(), i => Assert.Equal(ItemStatus.SUBMITTED, i.Status));

        var result = await _payments.ApproveAsync(all[0].Id, _collector.LineUserId);
        Assert.StartsWith("ยืนยันแล้ว", result);
        Db.ChangeTracker.Clear();
        Assert.All(await Db.BillItems.ToListAsync(), i => { Assert.Equal(ItemStatus.PAID, i.Status); Assert.Equal(i.AmountSatang, i.PaidSatang); });
        Assert.All(await Db.Bills.ToListAsync(), b => Assert.Equal(BillStatus.CLOSED, b.Status));
        Assert.StartsWith("รายการนี้ดำเนินการไปแล้ว", await _payments.ApproveAsync(all[1].Id, _collector.LineUserId));   // cannot be approved twice
    }

    [Fact]
    public async Task A_batch_must_be_one_collector_and_exactly_the_total()
    {
        var mine = await Bill(_collector, "ค่าข้าว", 10000);
        var mine2 = await Bill(_collector, "ค่าน้ำ", 5000);
        var theirs = await Bill(_other, "ค่ากาแฟ", 4000);

        Assert.False((await _payments.SubmitSlipBatchAsync(_me.LineUserId, new[] { mine, theirs }, 14000, SlipImage(20))).Ok);   // two payees
        Assert.False((await _payments.SubmitSlipBatchAsync(_me.LineUserId, new[] { mine, mine2 }, 10000, SlipImage(21))).Ok);    // not the sum
        Assert.False((await _payments.SubmitSlipBatchAsync(_me.LineUserId, new[] { "nope" }, 100, SlipImage(22))).Ok);           // not a bill of this user
        Assert.Equal(0, await Db.Payments.CountAsync());
    }

    [Fact]
    public async Task A_single_bill_still_accepts_a_partial_amount_and_rejection_frees_every_bill_of_the_batch()
    {
        var t1 = await Bill(_collector, "ค่าข้าว", 10000);
        var partial = await _payments.SubmitSlipBatchAsync(_me.LineUserId, new[] { t1 }, 4000, SlipImage(30));
        Assert.True(partial.Ok);
        var p = await Db.Payments.SingleAsync();
        Assert.Null(p.BatchId);
        Assert.Equal(4000, p.AmountSatang);
        Assert.StartsWith("ปฏิเสธแล้ว", await _payments.RejectAsync(p.Id, _collector.LineUserId));

        var t2 = await Bill(_collector, "ค่าน้ำ", 5000);
        Assert.True((await _payments.SubmitSlipBatchAsync(_me.LineUserId, new[] { t1, t2 }, 15000, SlipImage(31))).Ok);   // a rejected slip pays nothing: 10000 still owed on t1 + 5000
        var batch = await Db.Payments.Where(x => x.BatchId != null).ToListAsync();
        Assert.StartsWith("ปฏิเสธแล้ว", await _payments.RejectAsync(batch[0].Id, _collector.LineUserId));
        Db.ChangeTracker.Clear();
        Assert.All(await Db.Payments.Where(x => x.BatchId != null).ToListAsync(), x => Assert.Equal(PaymentStatus.REJECTED, x.Status));
    }
}
