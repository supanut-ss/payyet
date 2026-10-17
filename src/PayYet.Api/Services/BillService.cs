using Microsoft.EntityFrameworkCore;
using PayYet.Data;
using PayYet.Domain;

namespace PayYet.Services;

public record AddChargeResult(Bill Bill, BillCharge Charge, bool Merged);


public class BillService(AppDbContext db)
{
    /// <summary>
    /// Creates a new bill (one charge, one item per debtor). Every bill stands on its own: nothing is merged into an
    /// earlier bill, so each one has its own card, its own payments and its own history. Reminders are what group them.
    /// </summary>
    public async Task<AddChargeResult> AddChargeAsync(long creatorId, long? groupId, long? singleDebtorId,
        string title, SplitMode mode, IReadOnlyList<(long UserId, long Satang)> shares)
    {
        await using var tx = await db.Database.BeginTransactionAsync();

        var bill = new Bill
        {
            Token = Token.New(),
            CreatorId = creatorId,
            GroupId = groupId,
            Title = title,
            Status = BillStatus.OPEN,
            ScopeKey = null,            // legacy "one open bill per scope" key is no longer used
            RemindEveryDays = (await db.Users.AsNoTracking().Where(u => u.Id == creatorId).Select(u => (int?)u.RemindEveryDays).FirstOrDefaultAsync()) ?? 1,
            CreatedAt = DateTime.UtcNow
        };
        db.Bills.Add(bill);

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
            var item = new BillItem { Bill = bill, DebtorId = userId, Status = ItemStatus.PENDING, AmountSatang = satang };
            bill.Items.Add(item);
            charge.Shares.Add(new ChargeShare { Charge = charge, BillItem = item, AmountSatang = satang });
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new AddChargeResult(bill, charge, false);
    }

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
