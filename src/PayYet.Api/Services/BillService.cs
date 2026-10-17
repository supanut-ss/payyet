using Microsoft.EntityFrameworkCore;
using PayYet.Data;
using PayYet.Domain;

namespace PayYet.Services;

public record AddChargeResult(Bill Bill, BillCharge Charge, bool Merged);


public class BillService(AppDbContext db)
{
    public static string ScopeKey(long creatorId, long? groupId, long? debtorId) =>
        groupId.HasValue ? $"G:{creatorId}:{groupId}" : $"P:{creatorId}:{debtorId}";

    public Task<bool> HasOpenBillAsync(long creatorId, long? groupId, long? debtorId)
    {
        var key = ScopeKey(creatorId, groupId, debtorId);
        return db.Bills.AnyAsync(b => b.ScopeKey == key);
    }

    /// <summary>
    /// Adds a charge to the collector's open bill for this scope (group, or one debtor), creating the bill if none is open.
    /// Debtor totals are recalculated: a settled debtor re-opens with only the new remainder.
    /// </summary>
    public async Task<AddChargeResult> AddChargeAsync(long creatorId, long? groupId, long? singleDebtorId,
        string title, SplitMode mode, IReadOnlyList<(long UserId, long Satang)> shares)
    {
        var key = ScopeKey(creatorId, groupId, singleDebtorId);
        await using var tx = await db.Database.BeginTransactionAsync();

        var bill = await db.Bills.Include(b => b.Items).FirstOrDefaultAsync(b => b.ScopeKey == key);
        var merged = bill != null;
        if (bill == null)
        {
            bill = new Bill
            {
                Token = Token.New(),
                CreatorId = creatorId,
                GroupId = groupId,
                Title = title,
                Status = BillStatus.OPEN,
                ScopeKey = key,
                RemindEveryDays = (await db.Users.AsNoTracking().Where(u => u.Id == creatorId).Select(u => (int?)u.RemindEveryDays).FirstOrDefaultAsync()) ?? 1,
                CreatedAt = DateTime.UtcNow
            };
            db.Bills.Add(bill);
        }

        var charge = new BillCharge
        {
            Bill = bill,
            Title = title,
            TotalSatang = shares.Sum(s => s.Satang),
            SplitMode = mode,
            CreatedAt = DateTime.UtcNow
        };
        db.BillCharges.Add(charge);

        foreach (var (userId, satang) in shares)
        {
            var item = bill.Items.FirstOrDefault(i => i.DebtorId == userId);
            if (item == null)
            {
                item = new BillItem { Bill = bill, DebtorId = userId, Status = ItemStatus.PENDING };
                bill.Items.Add(item);
            }
            item.AmountSatang += satang;
            if (item.Status is ItemStatus.PAID or ItemStatus.WAIVED && item.PaidSatang < item.AmountSatang)
            {
                item.Status = ItemStatus.PENDING;
                item.PaidAt = null;
            }
            charge.Shares.Add(new ChargeShare { Charge = charge, BillItem = item, AmountSatang = satang });
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new AddChargeResult(bill, charge, merged);
    }

    /// <summary>Undo a charge within 10 minutes as long as nobody has paid or submitted a slip on the affected items.</summary>
    public async Task<(bool Ok, string Message, Bill? Bill)> UndoChargeAsync(long chargeId, long creatorId)
    {
        var charge = await db.BillCharges
            .Include(c => c.Bill).ThenInclude(b => b.Items).ThenInclude(i => i.Payments)
            .Include(c => c.Shares)
            .FirstOrDefaultAsync(c => c.Id == chargeId);
        if (charge == null || charge.Bill.CreatorId != creatorId) return (false, "ไม่พบรายการนี้", null);
        if (DateTime.UtcNow - charge.CreatedAt > TimeSpan.FromMinutes(10)) return (false, "เลยเวลายกเลิก 10 นาทีแล้ว", charge.Bill);

        var itemIds = charge.Shares.Select(s => s.BillItemId).ToHashSet();
        var touched = charge.Bill.Items.Where(i => itemIds.Contains(i.Id)).ToList();
        if (touched.Any(i => i.PaidSatang > 0 || i.Payments.Count > 0 || i.Status == ItemStatus.SUBMITTED))
            return (false, "ยกเลิกไม่ได้ เพราะมีคนจ่ายหรือส่งสลิปแล้ว", charge.Bill);

        var bill = charge.Bill;
        foreach (var share in charge.Shares)
        {
            var item = touched.First(i => i.Id == share.BillItemId);
            item.AmountSatang -= share.AmountSatang;
        }
        db.ChargeShares.RemoveRange(charge.Shares);
        db.BillCharges.Remove(charge);
        await db.SaveChangesAsync();

        var emptyItems = touched.Where(i => i.AmountSatang <= 0).ToList();
        if (emptyItems.Count > 0)
        {
            db.BillItems.RemoveRange(emptyItems);
            foreach (var i in emptyItems) bill.Items.Remove(i);
        }
        if (!await db.BillCharges.AnyAsync(c => c.BillId == bill.Id))
        {
            bill.Status = BillStatus.CANCELLED;
            bill.ScopeKey = null;
        }
        await db.SaveChangesAsync();
        return (true, "ยกเลิกรายการแล้ว", bill);
    }

    /// <summary>Closes the bill when every item is settled. Returns true if it was closed just now.</summary>
    public async Task<bool> CloseIfDoneAsync(Bill bill)
    {
        if (bill.Status != BillStatus.OPEN) return false;
        var items = await db.BillItems.Where(i => i.BillId == bill.Id).ToListAsync();
        if (items.Count == 0 || !items.All(i => i.IsSettled)) return false;
        bill.Status = BillStatus.CLOSED;
        bill.ScopeKey = null;
        await db.SaveChangesAsync();
        return true;
    }
}
