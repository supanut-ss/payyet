using PayYet.Data;
using PayYet.Line;

namespace PayYet.Services;

public record CreatedBills(string Summary, List<(string Label, long ChargeId)> Undo);

/// <summary>
/// Turns a confirmed draft into real bills and announces them (group card or private cards), and undoes a charge.
/// Shared by the chat flow (confirm button) and the LIFF "new bill" form so both behave identically.
/// </summary>
public class BillCreationService(AppDbContext db, LineClient line, BillService bills, BillCardBuilder cards)
{
    public async Task<CreatedBills> CreateAsync(AppUser user, Draft d)
    {
        var undo = new List<(string, long)>();
        string summary;

        if (d.Dest == "G")
        {
            var group = await db.Groups.FindAsync(d.GroupId);
            var r = await bills.AddChargeAsync(user.Id, d.GroupId, null, d.Title, d.Mode, d.ComputeShares());
            var pushed = group != null && await line.PushAsync(group.LineGroupId,
                await cards.BuildAsync(r.Bill, r.Merged ? "➕ เพิ่มรายการ" : "💰 เรียกเก็บเงิน"));
            undo.Add(("ยกเลิกรายการนี้", r.Charge.Id));
            summary = pushed
                ? $"✅ ส่งการ์ดเก็บเงินเข้ากลุ่ม {d.GroupName} แล้ว"
                : "⚠️ สร้างบิลแล้ว แต่ส่งเข้ากลุ่มไม่สำเร็จ (บอทยังอยู่ในกลุ่มหรือไม่?)";
        }
        else
        {
            var unreachable = new List<string>();
            foreach (var share in d.ComputeShares())
            {
                var debtor = await db.Users.FindAsync(share.UserId);
                var r = await bills.AddChargeAsync(user.Id, null, share.UserId, d.Title, d.Mode, new[] { share });
                if (debtor is { IsFriend: true })
                    await line.PushAsync(debtor.LineUserId, await cards.BuildAsync(r.Bill, r.Merged ? "➕ เพิ่มรายการ" : "💰 เรียกเก็บเงิน", debtor.Id));
                else
                    unreachable.Add(debtor?.Name ?? "?");
                if (undo.Count < 3) undo.Add(($"ยกเลิก {debtor?.Name}", r.Charge.Id));
            }
            summary = "✅ สร้างบิลส่วนตัวแล้ว";
            if (unreachable.Count > 0)
                summary += $"\n⚠️ ส่งแจ้งไม่ได้ (ยังไม่ได้เพิ่มบอทเป็นเพื่อน): {string.Join(", ", unreachable)}\nให้เขาเพิ่มบอทเป็นเพื่อน แล้วจะได้รับการเตือนรายวัน";
        }

        user.LastGroupId = d.GroupId ?? user.LastGroupId;
        await db.SaveChangesAsync();
        return new CreatedBills(summary, undo);
    }

    /// <summary>The 10-minute undo of the bill just created; tells the group when it succeeds.</summary>
    public async Task<(bool Ok, string Message)> UndoAsync(AppUser user, long chargeId)
    {
        var (ok, message, bill) = await bills.UndoChargeAsync(chargeId, user.Id);
        if (!ok || bill == null) return (ok, message);

        if (bill.GroupId.HasValue)
        {
            var group = await db.Groups.FindAsync(bill.GroupId.Value);
            if (group is { Active: true })
            {
                if (bill.Status == BillStatus.OPEN)
                    await line.PushAsync(group.LineGroupId, await cards.BuildAsync(bill, "↩️ ยกเลิกรายการล่าสุด"));
                else
                    await line.PushAsync(group.LineGroupId, Msg.Text($"↩️ {user.Name} ยกเลิกบิลแล้ว"));
            }
        }
        return (true, message);
    }
}
