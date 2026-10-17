using Microsoft.EntityFrameworkCore;
using PayYet.Data;
using PayYet.Domain;

namespace PayYet.Services;

public record ManageResult(bool Ok, string Message, Bill? Bill = null, BillItem? Item = null)
{
    public static ManageResult Fail(string message) => new(false, message);
}

/// <summary>
/// What a collector can do with a bill after it was created: record a cash payment, waive a person's debt,
/// cancel one charge or cancel the whole bill. Every method checks that the caller is the bill's creator.
/// </summary>
public class BillManageService(AppDbContext db, BillService bills)
{
    private const string NotFound = "ไม่พบรายการนี้";
    private const string PendingSlip = "มีสลิปรอตรวจอยู่ ให้กดยืนยันหรือปฏิเสธสลิปในแชทก่อน";

    /// <summary>Records money received outside the bot (cash, another bank). Default = everything still owed.</summary>
    public async Task<ManageResult> MarkPaidAsync(long itemId, long collectorId, long? amountSatang = null)
    {
        var item = await LoadItem(itemId, collectorId);
        if (item == null) return ManageResult.Fail(NotFound);
        if (item.Bill.Status != BillStatus.OPEN) return ManageResult.Fail("บิลนี้ปิดแล้ว");
        if (item.Status == ItemStatus.SUBMITTED) return ManageResult.Fail(PendingSlip);
        if (item.IsSettled) return ManageResult.Fail("รายการนี้จ่ายครบหรือยกหนี้ไปแล้ว");

        var amount = amountSatang ?? item.Remaining;
        if (amount <= 0 || amount > item.Remaining)
            return ManageResult.Fail($"ยอดต้องไม่เกินที่ค้างอยู่ (฿{Money.Format(item.Remaining)})");

        var now = DateTime.UtcNow;
        db.Payments.Add(new Payment   // audit trail; there is no slip image
        {
            BillItemId = item.Id,
            SlipPath = "cash",
            SlipHash = Token.Hex(32),
            MediaToken = Token.Hex(24),
            AmountSatang = amount,
            VerifiedBy = "CASH",
            Status = PaymentStatus.APPROVED,
            CreatedAt = now
        });
        item.PaidSatang += amount;
        if (item.PaidSatang >= item.AmountSatang) { item.Status = ItemStatus.PAID; item.PaidAt = now; }
        else item.Status = ItemStatus.PENDING;   // a rejected slip becomes payable again
        await db.SaveChangesAsync();
        await bills.CloseIfDoneAsync(item.Bill);

        var rest = item.Remaining;
        return new ManageResult(true,
            rest == 0 ? $"บันทึกแล้ว: {item.Debtor?.Name} จ่ายครบ" : $"บันทึกแล้ว: {item.Debtor?.Name} จ่าย ฿{Money.Format(amount)} ยังค้าง ฿{Money.Format(rest)}",
            item.Bill, item);
    }

    /// <summary>The collector forgives what a person still owes (the paid part stays paid).</summary>
    public async Task<ManageResult> WaiveAsync(long itemId, long collectorId)
    {
        var item = await LoadItem(itemId, collectorId);
        if (item == null) return ManageResult.Fail(NotFound);
        if (item.Bill.Status != BillStatus.OPEN) return ManageResult.Fail("บิลนี้ปิดแล้ว");
        if (item.Status == ItemStatus.SUBMITTED) return ManageResult.Fail(PendingSlip);
        if (item.IsSettled) return ManageResult.Fail("รายการนี้จ่ายครบหรือยกหนี้ไปแล้ว");

        item.Status = ItemStatus.WAIVED;
        await db.SaveChangesAsync();
        await bills.CloseIfDoneAsync(item.Bill);
        return new ManageResult(true, $"ยกหนี้ให้ {item.Debtor?.Name} แล้ว", item.Bill, item);
    }

    /// <summary>
    /// Cancels one charge of an open bill (no time limit). Everyone's amount shrinks by their share; it is refused when somebody
    /// already paid more than the new amount or has a slip waiting - waive that person instead.
    /// </summary>
    public async Task<ManageResult> CancelChargeAsync(long chargeId, long collectorId)
    {
        var charge = await db.BillCharges
            .Include(c => c.Bill).ThenInclude(b => b.Creator)
            .Include(c => c.Bill).ThenInclude(b => b.Items).ThenInclude(i => i.Debtor)
            .Include(c => c.Bill).ThenInclude(b => b.Items).ThenInclude(i => i.Payments)
            .Include(c => c.Shares)
            .FirstOrDefaultAsync(c => c.Id == chargeId);
        if (charge == null || charge.Bill.CreatorId != collectorId) return ManageResult.Fail(NotFound);
        var bill = charge.Bill;
        if (bill.Status != BillStatus.OPEN) return ManageResult.Fail("บิลนี้ปิดแล้ว");

        var affected = charge.Shares.Select(s => (Item: bill.Items.First(i => i.Id == s.BillItemId), Share: s.AmountSatang)).ToList();
        foreach (var (item, share) in affected)
        {
            if (item.Status == ItemStatus.SUBMITTED) return ManageResult.Fail($"{item.Debtor?.Name} มีสลิปรอตรวจอยู่ ให้ยืนยันหรือปฏิเสธสลิปก่อน");
            if (item.PaidSatang > item.AmountSatang - share)
                return ManageResult.Fail($"{item.Debtor?.Name} จ่ายเกินยอดใหม่แล้ว ให้ \"ยกหนี้\" ของคนนี้แทน");
        }

        foreach (var (item, share) in affected)
        {
            item.AmountSatang -= share;
            if (item.AmountSatang <= 0)
            {
                if (item.Payments.Count == 0) { db.BillItems.Remove(item); bill.Items.Remove(item); }
                else item.Status = ItemStatus.WAIVED;   // rejected slips still point at this row
            }
            else if (!item.IsSettled && item.PaidSatang >= item.AmountSatang)
            {
                item.Status = ItemStatus.PAID;
                item.PaidAt = DateTime.UtcNow;
            }
        }
        db.ChargeShares.RemoveRange(charge.Shares);
        db.BillCharges.Remove(charge);
        await db.SaveChangesAsync();

        if (!await db.BillCharges.AnyAsync(c => c.BillId == bill.Id))
        {
            bill.Status = BillStatus.CANCELLED;
            bill.ScopeKey = null;
            await db.SaveChangesAsync();
            return new ManageResult(true, "ยกเลิกรายการแล้ว และเนื่องจากไม่เหลือรายการ บิลนี้ถูกยกเลิกด้วย", bill);
        }
        await bills.CloseIfDoneAsync(bill);
        return new ManageResult(true, $"ยกเลิกรายการ \"{charge.Title}\" แล้ว", bill);
    }

    /// <summary>
    /// Renames one charge and/or changes what each person owes for it (<paramref name="newShares"/>: bill item id → new share in satang).
    /// Refused while a slip waits for that person or when they already paid more than the new amount.
    /// </summary>
    public async Task<ManageResult> EditChargeAsync(long chargeId, long collectorId, string? title, IReadOnlyDictionary<long, long>? newShares)
    {
        var charge = await db.BillCharges
            .Include(c => c.Bill).ThenInclude(b => b.Items).ThenInclude(i => i.Debtor)
            .Include(c => c.Shares)
            .FirstOrDefaultAsync(c => c.Id == chargeId);
        if (charge == null || charge.Bill.CreatorId != collectorId) return ManageResult.Fail(NotFound);
        var bill = charge.Bill;
        if (bill.Status != BillStatus.OPEN) return ManageResult.Fail("บิลนี้ปิดแล้ว");

        title = title?.Trim();
        if (!string.IsNullOrEmpty(title) && title.Length > 80) title = title[..80];

        var changes = new List<(BillItem Item, ChargeShare Share, long Delta, long NewShare)>();
        foreach (var (itemId, satang) in newShares ?? new Dictionary<long, long>())
        {
            var share = charge.Shares.FirstOrDefault(s => s.BillItemId == itemId);
            if (share == null) return ManageResult.Fail("ไม่พบคนนี้ในรายการ");
            if (satang <= 0 || satang > 100_000_000) return ManageResult.Fail("ยอดต้องมากกว่า 0 และไม่เกิน 1,000,000 บาท");
            if (satang == share.AmountSatang) continue;

            var item = bill.Items.First(i => i.Id == itemId);
            if (item.Status == ItemStatus.SUBMITTED) return ManageResult.Fail($"{item.Debtor?.Name} มีสลิปรอตรวจอยู่ ให้ยืนยันหรือปฏิเสธสลิปก่อน");
            if (item.PaidSatang > item.AmountSatang - share.AmountSatang + satang)
                return ManageResult.Fail($"{item.Debtor?.Name} จ่ายเกินยอดใหม่แล้ว ให้ \"ยกหนี้\" ของคนนี้แทน");
            changes.Add((item, share, satang - share.AmountSatang, satang));
        }

        if (string.IsNullOrEmpty(title) && changes.Count == 0) return ManageResult.Fail("ไม่มีอะไรเปลี่ยนแปลง");

        if (!string.IsNullOrEmpty(title)) charge.Title = title;
        var now = DateTime.UtcNow;
        foreach (var (item, share, delta, newShare) in changes)
        {
            share.AmountSatang = newShare;
            item.AmountSatang += delta;
            if (item.Status == ItemStatus.WAIVED) continue;
            if (item.PaidSatang >= item.AmountSatang) { item.Status = ItemStatus.PAID; item.PaidAt ??= now; }
            else if (item.Status == ItemStatus.PAID) { item.Status = ItemStatus.PENDING; item.PaidAt = null; }   // owes more again
        }
        if (changes.Count > 0)
        {
            charge.TotalSatang = charge.Shares.Sum(s => s.AmountSatang);
            charge.SplitMode = SplitMode.CUSTOM;
        }
        await db.SaveChangesAsync();
        await bills.CloseIfDoneAsync(bill);
        return new ManageResult(true, changes.Count > 0 ? $"แก้ไขรายการ \"{charge.Title}\" แล้ว" : "เปลี่ยนชื่อรายการแล้ว", bill);
    }

    /// <summary>Cancels the whole bill. Money already paid is not touched (settle it with the payer yourself).</summary>
    public async Task<ManageResult> CancelBillAsync(long billId, long collectorId)
    {
        var bill = await db.Bills.Include(b => b.Items).FirstOrDefaultAsync(b => b.Id == billId);
        if (bill == null || bill.CreatorId != collectorId) return ManageResult.Fail(NotFound);
        if (bill.Status != BillStatus.OPEN) return ManageResult.Fail("บิลนี้ปิดไปแล้ว");

        bill.Status = BillStatus.CANCELLED;
        bill.ScopeKey = null;
        await db.SaveChangesAsync();
        var paid = bill.Items.Where(i => i.PaidSatang > 0).Sum(i => i.PaidSatang);
        return new ManageResult(true, paid > 0
            ? $"ยกเลิกบิลแล้ว (มีเงินที่รับไปแล้ว ฿{Money.Format(paid)} ระบบไม่ได้คืนเงินให้ ต้องคืนกันเอง)"
            : "ยกเลิกบิลแล้ว", bill);
    }

    /// <summary>The collector's open bills with per-person state, newest first (for the manage page).</summary>
    public Task<List<Bill>> OpenBillsAsync(long collectorId) => db.Bills
        .Include(b => b.Group)
        .Include(b => b.Charges).ThenInclude(c => c.Shares)
        .Include(b => b.Items).ThenInclude(i => i.Debtor)
        .Include(b => b.Items).ThenInclude(i => i.Payments)
        .Where(b => b.CreatorId == collectorId && b.Status == BillStatus.OPEN)
        .OrderByDescending(b => b.Id).Take(20).ToListAsync();

    private Task<BillItem?> LoadItem(long itemId, long collectorId) => db.BillItems
        .Include(i => i.Bill).Include(i => i.Debtor)
        .FirstOrDefaultAsync(i => i.Id == itemId && i.Bill.CreatorId == collectorId);
}
