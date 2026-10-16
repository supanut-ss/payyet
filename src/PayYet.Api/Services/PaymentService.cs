using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;

namespace PayYet.Services;

public class PaymentService(AppDbContext db, LineClient line, BillService bills, IOptions<AppSettings> options,
    IWebHostEnvironment env)
{
    public const long MaxSlipBytes = 5 * 1024 * 1024;

    public static string? DetectImageType(byte[] d)
    {
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        return null;
    }

    public string SlipRoot => Path.Combine(env.ContentRootPath, "App_Data", "slips");

    /// <summary>Debtor submits a slip from the LIFF page. The collector is asked to confirm in the private chat.</summary>
    public async Task<(bool Ok, string Message)> SubmitSlipAsync(string lineUserId, string billToken, long amountSatang, byte[] data)
    {
        var type = DetectImageType(data);
        if (type == null) return (false, "ไฟล์ต้องเป็นรูป JPEG หรือ PNG");
        if (data.Length > MaxSlipBytes) return (false, "ไฟล์ใหญ่เกิน 5 MB");

        var item = await db.BillItems
            .Include(i => i.Bill).ThenInclude(b => b.Creator)
            .Include(i => i.Debtor)
            .FirstOrDefaultAsync(i => i.Bill.Token == billToken && i.Debtor!.LineUserId == lineUserId);
        if (item == null) return (false, "คุณไม่อยู่ในบิลนี้");
        if (item.Bill.Status != BillStatus.OPEN) return (false, "บิลนี้ปิดแล้ว");
        if (item.IsSettled) return (false, "คุณจ่ายครบแล้ว");
        if (item.Status == ItemStatus.SUBMITTED) return (false, "มีสลิปที่รอผู้เก็บตรวจอยู่แล้ว");
        if (amountSatang <= 0 || amountSatang > item.Remaining)
            return (false, $"ยอดไม่ถูกต้อง (คงเหลือ {Money.Format(item.Remaining)} บาท)");

        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (await db.Payments.AnyAsync(p => p.SlipHash == hash)) return (false, "สลิปนี้เคยถูกส่งแล้ว");

        var ext = type == "image/png" ? ".png" : ".jpg";
        var rel = Path.Combine(DateTime.UtcNow.ToString("yyyyMM"), hash[..32] + ext);
        var full = Path.Combine(SlipRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, data);

        var payment = new Payment
        {
            BillItemId = item.Id,
            SlipPath = rel,
            SlipHash = hash,
            MediaToken = Token.Hex(),
            AmountSatang = amountSatang,
            Status = PaymentStatus.SUBMITTED,
            CreatedAt = DateTime.UtcNow
        };
        db.Payments.Add(payment);
        item.Status = ItemStatus.SUBMITTED;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return (false, "สลิปนี้เคยถูกส่งแล้ว"); }

        var imageUrl = $"{options.Value.App.PublicBaseUrl.TrimEnd('/')}/media/{payment.MediaToken}";
        var who = item.Debtor!.Name;
        await line.PushAsync(item.Bill.Creator.LineUserId,
            Cards.SlipReview(options.Value, who, amountSatang, item.Bill.Title, imageUrl, payment.Id));
        return (true, "ส่งสลิปแล้ว รอผู้เก็บตรวจสอบ");
    }

    public async Task<string> ApproveAsync(long paymentId, string byLineUserId)
    {
        var p = await Load(paymentId);
        if (p == null || p.BillItem.Bill.Creator.LineUserId != byLineUserId) return "ไม่พบรายการนี้";
        if (p.Status != PaymentStatus.SUBMITTED) return "รายการนี้ดำเนินการไปแล้ว";

        var item = p.BillItem;
        p.Status = PaymentStatus.APPROVED;
        p.VerifiedBy = "CREATOR";
        item.PaidSatang += p.AmountSatang;
        if (item.PaidSatang >= item.AmountSatang) { item.Status = ItemStatus.PAID; item.PaidAt = DateTime.UtcNow; }
        else item.Status = ItemStatus.PENDING;
        await db.SaveChangesAsync();

        var bill = item.Bill;
        if (item.Debtor is { IsFriend: true })
            await line.PushAsync(item.Debtor.LineUserId, Msg.Text(
                $"✅ ผู้เก็บยืนยันรับเงิน ฿{Money.Format(p.AmountSatang)} แล้ว\nบิล: {bill.Title}" +
                (item.Status == ItemStatus.PAID ? "\nคุณจ่ายครบแล้ว ขอบคุณครับ" : $"\nคงเหลือ ฿{Money.Format(item.Remaining)}")));

        if (await bills.CloseIfDoneAsync(bill) && bill.GroupId.HasValue)
        {
            var group = await db.Groups.FindAsync(bill.GroupId.Value);
            if (group is { Active: true })
                await line.PushAsync(group.LineGroupId, Msg.Text($"🎉 ทุกคนจ่ายครบแล้ว — บิลของ {bill.Creator.Name} ปิดเรียบร้อย"));
        }
        return $"ยืนยันแล้ว ✅ {item.Debtor?.Name} ฿{Money.Format(p.AmountSatang)}";
    }

    public async Task<string> RejectAsync(long paymentId, string byLineUserId)
    {
        var p = await Load(paymentId);
        if (p == null || p.BillItem.Bill.Creator.LineUserId != byLineUserId) return "ไม่พบรายการนี้";
        if (p.Status != PaymentStatus.SUBMITTED) return "รายการนี้ดำเนินการไปแล้ว";

        p.Status = PaymentStatus.REJECTED;
        p.BillItem.Status = ItemStatus.REJECTED;
        await db.SaveChangesAsync();

        var debtor = p.BillItem.Debtor;
        if (debtor is { IsFriend: true })
            await line.PushAsync(debtor.LineUserId, Msg.Text($"❌ ผู้เก็บไม่ยืนยันสลิปของคุณ (บิล: {p.BillItem.Bill.Title})\nกรุณาตรวจสอบแล้วส่งสลิปใหม่อีกครั้ง"));
        return $"ปฏิเสธแล้ว ❌ {debtor?.Name}";
    }

    private Task<Payment?> Load(long id) => db.Payments
        .Include(p => p.BillItem).ThenInclude(i => i.Bill).ThenInclude(b => b.Creator)
        .Include(p => p.BillItem).ThenInclude(i => i.Debtor)
        .FirstOrDefaultAsync(p => p.Id == id);
}
