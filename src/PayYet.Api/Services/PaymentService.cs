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

    /// <summary>Debtor submits a slip for one bill (the amount may be partial). The collector is asked to confirm in the private chat.</summary>
    public Task<(bool Ok, string Message)> SubmitSlipAsync(string lineUserId, string billToken, long amountSatang, byte[] data) =>
        SubmitSlipBatchAsync(lineUserId, new[] { billToken }, amountSatang, data);

    /// <summary>
    /// One slip paying one or several bills of the SAME collector (one transfer = one payee). With several bills the amount must be
    /// exactly what they still owe together; with one bill it may be partial. The collector gets a single card and approves them all at once.
    /// </summary>
    public async Task<(bool Ok, string Message)> SubmitSlipBatchAsync(string lineUserId, IReadOnlyList<string> billTokens, long amountSatang, byte[] data)
    {
        var type = DetectImageType(data);
        if (type == null) return (false, "ไฟล์ต้องเป็นรูป JPEG หรือ PNG");
        if (data.Length > MaxSlipBytes) return (false, "ไฟล์ใหญ่เกิน 5 MB");
        if (SlipInspector.TryGetSize(data) is { } size && Math.Min(size.Width, size.Height) < MinSlipSide)
            return (false, "รูปเล็กเกินไป กรุณาแนบรูปสลิปเต็มใบจากแอปธนาคาร");

        var tokens = billTokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        if (tokens.Count == 0) return (false, "ยังไม่ได้เลือกบิลที่จะจ่าย");
        if (tokens.Count > 20) return (false, "เลือกได้ไม่เกิน 20 บิลต่อสลิป");

        var items = await db.BillItems
            .Include(i => i.Bill).ThenInclude(b => b.Creator)
            .Include(i => i.Debtor)
            .Where(i => tokens.Contains(i.Bill.Token) && i.Debtor!.LineUserId == lineUserId)
            .OrderBy(i => i.BillId).ToListAsync();
        if (items.Count != tokens.Count) return (false, "คุณไม่อยู่ในบิลที่เลือก");
        if (items.Select(i => i.Bill.CreatorId).Distinct().Count() > 1) return (false, "สลิปหนึ่งใบจ่ายได้เฉพาะบิลของผู้เก็บคนเดียวกัน");
        foreach (var i in items)
        {
            if (i.Bill.Status != BillStatus.OPEN) return (false, $"บิล \"{i.Bill.Title}\" ปิดแล้ว");
            if (i.IsSettled) return (false, $"คุณจ่ายบิล \"{i.Bill.Title}\" ครบแล้ว");
            if (i.Status == ItemStatus.SUBMITTED) return (false, $"บิล \"{i.Bill.Title}\" มีสลิปที่รอผู้เก็บตรวจอยู่แล้ว");
        }

        var owed = items.Sum(i => i.Remaining);
        if (items.Count == 1)
        {
            if (amountSatang <= 0 || amountSatang > owed) return (false, $"ยอดไม่ถูกต้อง (คงเหลือ {Money.Format(owed)} บาท)");
        }
        else if (amountSatang != owed)
            return (false, $"ยอดต้องเท่ากับยอดรวมของบิลที่เลือก ฿{Money.Format(owed)}");

        var since = DateTime.UtcNow.AddHours(-24);
        var itemIds = items.Select(i => i.Id).ToList();
        if (await db.Payments.CountAsync(p => itemIds.Contains(p.BillItemId) && p.CreatedAt >= since && p.SlipPath != "cash") >= MaxSlipsPerDay * items.Count)
            return (false, "ส่งสลิปบ่อยเกินไป ลองใหม่พรุ่งนี้ หรือติดต่อผู้เก็บเงินโดยตรง");

        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (await db.Payments.AnyAsync(p => p.SlipHash == hash)) return (false, "สลิปนี้เคยถูกส่งแล้ว");

        var newestBill = items.Max(i => i.Bill.CreatedAt);
        var oldestBill = items.Min(i => i.Bill.CreatedAt);
        // Optional bank check: the same transaction can never pay two bills, and the amount must match what the payer declared.
        var check = await verifier.VerifyAsync(data, type, amountSatang);
        if (!check.Checked) check = qrVerifier.Verify(data, oldestBill);   // no provider: read the slip's own QR instead
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

        // All payments of this slip share one image; only the first carries the unique hash / bank reference (the others get a derived hash).
        var batchId = items.Count > 1 ? Token.Hex(16) : null;
        var payments = new List<Payment>();
        for (var k = 0; k < items.Count; k++)
        {
            var item = items[k];
            var payment = new Payment
            {
                BillItemId = item.Id,
                SlipPath = rel,
                SlipHash = k == 0 ? hash : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hash + ":" + item.Id))).ToLowerInvariant(),
                MediaToken = Token.Hex(),
                AmountSatang = items.Count == 1 ? amountSatang : item.Remaining,
                SlipRef = k == 0 ? check.Ref : null,
                VerifiedBy = check.Checked ? check.Source : null,
                BatchId = batchId,
                Status = PaymentStatus.SUBMITTED,
                CreatedAt = DateTime.UtcNow
            };
            payments.Add(payment);
            db.Payments.Add(payment);
            item.Status = ItemStatus.SUBMITTED;
        }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return (false, "สลิปนี้เคยถูกส่งแล้ว"); }

        var primary = payments[0];
        var imageUrl = $"{options.Value.App.PublicBaseUrl.TrimEnd('/')}/media/{primary.MediaToken}";
        var who = items[0].Debtor!.Name;
        var creator = items[0].Bill.Creator;
        var title = items.Count == 1 ? items[0].Bill.Title : $"{items.Count} บิล: " + string.Join(", ", items.Select(i => i.Bill.Title));
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
        if (items.Count == 1 && amountSatang < items[0].Remaining)
            notes.Add(($"⚠️ ผู้จ่ายแจ้งจ่ายบางส่วน ฿{Money.Format(amountSatang)} จากที่ค้าง ฿{Money.Format(items[0].Remaining)}", Theme.Amber));
        if (items.Count > 1)
            notes.Add(($"สลิปใบเดียวจ่าย {items.Count} บิล กดยืนยันครั้งเดียวนับเงินทุกบิล", Theme.Muted));
        var delivered = await line.PushAsync(creator.LineUserId, Cards.SlipReview(options.Value, who, amountSatang, title, imageUrl, primary.Id, notes));
        if (delivered) return (true, "ส่งสลิปแล้ว รอผู้เก็บตรวจสอบ");

        // The collector cannot be messaged (never added / blocked the bot): say so, and tell the group where the slip can still be approved.
        creator.IsFriend = false;
        await db.SaveChangesAsync();
        var groupId = items.Select(i => i.Bill.GroupId).FirstOrDefault(g => g.HasValue);
        if (groupId.HasValue)
        {
            var group = await db.Groups.FindAsync(groupId.Value);
            if (group is { Active: true })
                await line.PushAsync(group.LineGroupId, Msg.Text(
                    $"⚠️ {creator.Name} มีสลิปรอตรวจจาก {who} แต่บอทส่งข้อความหาไม่ได้ (ยังไม่ได้เพิ่มบอทเป็นเพื่อนหรือบล็อกอยู่)\nกรุณาเพิ่มบอทเป็นเพื่อน แล้วเปิดเมนู \"จัดการบิล\" เพื่อยืนยันสลิป"));
        }
        return (true, "⚠️ ส่งสลิปแล้ว แต่ผู้เก็บยังไม่ได้รับแจ้งเตือน (บอทส่งข้อความหาเขาไม่ได้) ลองแจ้งผู้เก็บให้เปิดหน้า \"จัดการบิล\" เพื่อยืนยันสลิป");
    }

    /// <summary>Approves a slip; when it paid several bills, every payment of that slip is approved together.</summary>
    public async Task<string> ApproveAsync(long paymentId, string byLineUserId)
    {
        var batch = await LoadBatch(paymentId, byLineUserId);
        if (batch == null) return "ไม่พบรายการนี้";
        if (batch.Any(p => p.Status != PaymentStatus.SUBMITTED)) return "รายการนี้ดำเนินการไปแล้ว";

        foreach (var p in batch)
        {
            var item = p.BillItem;
            p.Status = PaymentStatus.APPROVED;
            p.VerifiedBy ??= "CREATOR";   // keeps "BANK" when the bank already verified the slip
            item.PaidSatang += p.AmountSatang;
            if (item.PaidSatang >= item.AmountSatang) { item.Status = ItemStatus.PAID; item.PaidAt = DateTime.UtcNow; }
            else item.Status = ItemStatus.PENDING;
        }
        await db.SaveChangesAsync();

        var debtor = batch[0].BillItem.Debtor;
        var total = batch.Sum(p => p.AmountSatang);
        if (debtor is { IsFriend: true })
        {
            var last = batch[^1].BillItem;
            var text = batch.Count == 1
                ? $"✅ ผู้เก็บยืนยันรับเงิน ฿{Money.Format(total)} แล้ว\nบิล: {last.Bill.Title}" +
                  (last.Status == ItemStatus.PAID ? "\nคุณจ่ายครบแล้ว ขอบคุณครับ" : $"\nคงเหลือ ฿{Money.Format(last.Remaining)}")
                : $"✅ ผู้เก็บยืนยันรับเงิน ฿{Money.Format(total)} แล้ว ({batch.Count} บิล)\n" + string.Join("\n", batch.Select(p => "• " + p.BillItem.Bill.Title)) + "\nขอบคุณครับ";
            await line.PushAsync(debtor.LineUserId, Msg.Text(text));
        }

        foreach (var bill in batch.Select(p => p.BillItem.Bill).DistinctBy(b => b.Id))
            if (await bills.CloseIfDoneAsync(bill) && bill.GroupId.HasValue)
            {
                var group = await db.Groups.FindAsync(bill.GroupId.Value);
                if (group is { Active: true })
                    await line.PushAsync(group.LineGroupId, Msg.Text($"🎉 ทุกคนจ่ายครบแล้ว — บิล \"{bill.Title}\" ของ {bill.Creator.Name} ปิดเรียบร้อย"));
            }
        return $"ยืนยันแล้ว ✅ {debtor?.Name} ฿{Money.Format(total)}" + (batch.Count > 1 ? $" ({batch.Count} บิล)" : "");
    }

    public async Task<string> RejectAsync(long paymentId, string byLineUserId)
    {
        var batch = await LoadBatch(paymentId, byLineUserId);
        if (batch == null) return "ไม่พบรายการนี้";
        if (batch.Any(p => p.Status != PaymentStatus.SUBMITTED)) return "รายการนี้ดำเนินการไปแล้ว";

        foreach (var p in batch)
        {
            p.Status = PaymentStatus.REJECTED;
            p.BillItem.Status = ItemStatus.REJECTED;
        }
        await db.SaveChangesAsync();

        var debtor = batch[0].BillItem.Debtor;
        if (debtor is { IsFriend: true })
            await line.PushAsync(debtor.LineUserId, Msg.Text(
                $"❌ ผู้เก็บไม่ยืนยันสลิปของคุณ (บิล: {string.Join(", ", batch.Select(p => p.BillItem.Bill.Title))})\nกรุณาตรวจสอบแล้วส่งสลิปใหม่อีกครั้ง"));
        return $"ปฏิเสธแล้ว ❌ {debtor?.Name}";
    }

    /// <summary>The payment and every other payment made with the same slip, if it belongs to this collector. Null when not found.</summary>
    private async Task<List<Payment>?> LoadBatch(long paymentId, string byLineUserId)
    {
        var p = await Load(paymentId);
        if (p == null || p.BillItem.Bill.Creator.LineUserId != byLineUserId) return null;
        if (p.BatchId == null) return new List<Payment> { p };
        var all = await db.Payments
            .Include(x => x.BillItem).ThenInclude(i => i.Bill).ThenInclude(b => b.Creator)
            .Include(x => x.BillItem).ThenInclude(i => i.Debtor)
            .Where(x => x.BatchId == p.BatchId).OrderBy(x => x.Id).ToListAsync();
        return all.All(x => x.BillItem.Bill.Creator.LineUserId == byLineUserId) ? all : null;
    }

    private Task<Payment?> Load(long id) => db.Payments
        .Include(p => p.BillItem).ThenInclude(i => i.Bill).ThenInclude(b => b.Creator)
        .Include(p => p.BillItem).ThenInclude(i => i.Debtor)
        .FirstOrDefaultAsync(p => p.Id == id);
}
