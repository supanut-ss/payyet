using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;

namespace PayYet.Services;

public class BillCardBuilder(AppDbContext db, IOptions<AppSettings> options)
{
    public string LiffUrl(string billToken) => $"https://liff.line.me/{options.Value.Liff.Id}?b={billToken}";
    public string OweUrl() => $"https://liff.line.me/{options.Value.Liff.Id}?mode=owe";
    public string DetailUrl(long billId) => $"https://liff.line.me/{options.Value.Liff.Id}?mode=bill&id={billId}";

    /// <summary>The card for a group chat followed by a message tagging everyone on it who still has to pay.</summary>
    public async Task<object[]> BuildForGroupAsync(Bill bill, string headline)
    {
        var card = await BuildAsync(bill, headline);
        var debtors = await db.BillItems.Where(i => i.BillId == bill.Id && i.DebtorId != null)
            .Include(i => i.Debtor).ToListAsync();
        var owing = debtors.Where(i => !i.IsSettled && i.Debtor != null).Select(i => i.Debtor!.LineUserId);
        var tag = Msg.Mention(headline, owing);
        return tag == null ? new[] { card } : new[] { card, tag };
    }

    /// <summary>Flex card listing the bill's items and every person's remaining amount.</summary>
    public async Task<object> BuildAsync(Bill bill, string headline, long? onlyDebtorId = null)
    {
        var items = await db.BillItems.Include(i => i.Debtor)
            .Where(i => i.BillId == bill.Id).OrderBy(i => i.Id).ToListAsync();
        if (onlyDebtorId.HasValue) items = items.Where(i => i.DebtorId == onlyDebtorId).ToList();
        var charges = await db.BillCharges.Where(c => c.BillId == bill.Id).OrderBy(c => c.Id).ToListAsync();
        var creator = await db.Users.FindAsync(bill.CreatorId);

        var rows = items.Select(i => new Cards.Row(
            i.Debtor?.Name ?? "?",
            i.Status switch
            {
                ItemStatus.PAID => RowState.Paid,
                ItemStatus.WAIVED => RowState.Waived,
                ItemStatus.SUBMITTED => RowState.Submitted,
                _ => RowState.Pending
            },
            i.Remaining)).ToList();

        var kind = headline.Contains("ยังไม่จ่าย") ? "reminder" : headline.Contains("ยกเลิก") ? "undone" : headline.Contains("เพิ่ม") ? "added" : "new";
        var lines = charges.Select(c => new Cards.ChargeLine(c.Title, c.TotalSatang)).ToList();
        return Cards.Bill(options.Value, kind, headline, creator?.Name ?? "?", lines, rows, LiffUrl(bill.Token), DetailUrl(bill.Id));
    }
}
