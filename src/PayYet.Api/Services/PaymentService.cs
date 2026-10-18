using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;

namespace PayYet.Services;

public class PaymentService(AppDbContext db, LineClient line, BillService bills, IOptions<AppSettings> options,
    IWebHostEnvironment env, SlipVerifier verifier, SlipQrVerifier qrVerifier, SlipAmountChecker amountChecker)
{
    public const long MaxSlipBytes = 5 * 1024 * 1024;
    public const int MinSlipSide = 300;          // px: anything smaller cannot be a real slip screenshot
    public const int MaxSlipsPerDay = 4;         // per bill item, counting rejected ones

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
        if (SlipInspector.TryGetSize(data) is { } size && Math.Min(size.Width, size.Height) < MinSlipSide)
            return (false, "รูปเล็กเกินไป กรุณาแนบรูปสลิปเต็มใบจากแอปธนาคาร");

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

        var since = DateTime.UtcNow.AddHours(-24);
        if (await db.Payments.CountAsync(p => p.BillItemId == item.Id && p.CreatedAt >= since && p.SlipPath != "cash") >= MaxSlipsPerDay)
            return (false, "ส่งสลิปบ่อยเกินไป ลองใหม่พรุ่งนี้ หรือติดต่อผู้เก็บเงินโดยตรง");

        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (await db.Payments.AnyAsync(p => p.SlipHash == hash)) return (false, "สลิปนี้เคยถูกส่งแล้ว");

        // Optional bank check: the same transaction can never pay two bills, and the amount must match what the payer declared.
        var check = await verifier.VerifyAsync(data, type, amountSatang);
        if (!check.Checked) check = qrVerifier.Verify(data, item.Bill.CreatedAt);   // no provider: read the slip's own QR instead
        if (check.Checked && !check.Ok) return (false, check.Error ?? "ตรวจสลิปไม่ผ่าน");

        // The bank provider already confirmed the amount; otherwise read it off the slip and compare with what the payer declared.
        SlipAmountResult? amount = check.Source == "BANK" && check.Checked ? null : amountChecker.Check(data, amountSatang);
        if (amount is { Check: AmountCheck.Mismatch } && options.Value.SlipVerify.AmountCheck == "reject")
            return (false, $"ยอดในสลิป ({string.Join(", ", amount.Found.Select(v => "฿" + Money.Format((long)Math.Round(v * 100))))}) ไม่ตรงกับยอดที่แจ้ง ฿{Money.Format(amountSatang)} " +
                           "กรุณาแก้ยอดให้ตรงกับสลิป หรือแนบรูปสลิปเต็มใบที่ชัดเจน");
        if (check.Ref != null && await db.Payments.AnyAsync(p => p.SlipRef == check.Ref)) return (false, "สลิปนี้เคยถูกใช้แล้ว");

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
            SlipRef = check.Ref,
            VerifiedBy = check.Checked ? check.Source : null,
            Status = PaymentStatus.SUBMITTED,
            CreatedAt = DateTime.UtcNow
        };
        db.Payments.Add(payment);
        item.Status = ItemStatus.SUBMITTED;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return (false, "สลิปนี้เคยถูกส่งแล้ว"); }

        var imageUrl = $"{options.Value.App.PublicBaseUrl.TrimEnd('/')}/media/{payment.MediaToken}";
        var who = item.Debtor!.Name;
        var notes = new List<(string, string)>();
        if (check.Checked && check.Source == "BANK") notes.Add(("✅ ตรวจกับธนาคารแล้ว ยอดและเลขอ้างอิงถูกต้อง", Theme.Green));
        else if (check.Checked) notes.Add(($"✅ {check.Note} · โปรดเช็กยอดในแอปธนาคาร", Theme.Green));
        else notes.Add((check.Note ?? "ยังไม่ได้ตรวจกับธนาคาร โปรดเช็กยอดในสลิปก่อนกดยืนยัน", Theme.Muted));
        if (amount != null)
            notes.Add(amount.Check switch
            {
                AmountCheck.Match => ($"✅ ยอดในสลิปตรงกับที่แจ้ง ฿{Money.Format(amountSatang)}", Theme.Green),
                AmountCheck.Mismatch => ($"⚠️ ยอดในสลิปอ่านได้ {string.Join(", ", amount.Found.Select(v => "฿" + Money.Format((long)Math.Round(v * 100))))} ไม่ตรงกับที่แจ้ง ฿{Money.Format(amountSatang)}", Theme.Red),
                _ => ("อ่านยอดจากสลิปไม่ได้ โปรดเช็กยอดในสลิปเอง", Theme.Muted)
            });
        if (amountSatang < item.Remaining)
            notes.Add(($"⚠️ ผู้จ่ายแจ้งจ่ายบางส่วน ฿{Money.Format(amountSatang)} จากที่ค้าง ฿{Money.Format(item.Remaining)}", Theme.Amber));
        var delivered = await line.PushAsync(item.Bill.Creator.LineUserId,
            Cards.SlipReview(options.Value, who, amountSatang, item.Bill.Title, imageUrl, payment.Id, notes));
        if (delivered) return (true, "ส่งสลิปแล้ว รอผู้เก็บตรวจสอบ");

        // The collector cannot be messaged (never added / blocked the bot): say so, and tell the group where the slip can still be approved.
        var creator = item.Bill.Creator;
        creator.IsFriend = false;
        await db.SaveChangesAsync();
        if (item.Bill.GroupId.HasValue)
        {
            var group = await db.Groups.FindAsync(item.Bill.GroupId.Value);
            if (group is { Active: true })
                await line.PushAsync(group.LineGroupId, Msg.Text(
                    $"⚠️ {creator.Name} มีสลิปรอตรวจจาก {who} แต่บอทส่งข้อความหาไม่ได้ (ยังไม่ได้เพิ่มบอทเป็นเพื่อนหรือบล็อกอยู่)\nกรุณาเพิ่มบอทเป็นเพื่อน แล้วเปิดเมนู \"จัดการบิล\" เพื่อยืนยันสลิป"));
        }
        return (true, "⚠️ ส่งสลิปแล้ว แต่ผู้เก็บยังไม่ได้รับแจ้งเตือน (บอทส่งข้อความหาเขาไม่ได้) ลองแจ้งผู้เก็บให้เปิดหน้า \"จัดการบิล\" เพื่อยืนยันสลิป");
    }

    public async Task<string> ApproveAsync(long paymentId, string byLineUserId)
    {
        var p = await Load(paymentId);
        if (p == null || p.BillItem.Bill.Creator.LineUserId != byLineUserId) return "ไม่พบรายการนี้";
        if (p.Status != PaymentStatus.SUBMITTED) return "รายการนี้ดำเนินการไปแล้ว";

        var item = p.BillItem;
        p.Status = PaymentStatus.APPROVED;
        p.VerifiedBy ??= "CREATOR";   // keeps "BANK" when the bank already verified the slip
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
