using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;

namespace PayYet.Services;

/// <summary>The confirmation card shown to the collector before a bill is created (shared by the chat flow and the people-picker page).</summary>
public class DraftCardBuilder(BillService bills, IOptions<AppSettings> options)
{
    public async Task<object> BuildAsync(AppUser user, Draft d)
    {
        var shares = d.ComputeShares();
        var merging = d.Dest == "G"
            ? await bills.HasOpenBillAsync(user.Id, d.GroupId, null)
            : (await Task.WhenAll(d.Debtors.Select(x => bills.HasOpenBillAsync(user.Id, null, x.UserId)))).Any(x => x);

        var calc = new List<string>();
        if (d.Custom)
            calc.AddRange(d.Debtors.Select(x => $"{x.Name}  ฿{Money.Format(x.Satang ?? 0)}"));
        else if (d.PerPerson)
            calc.Add($"คนละ ฿{Money.Format(d.AmountSatang)} × {d.Debtors.Count} คน = ฿{Money.Format(d.Total)}");
        else
        {
            var even = d.AmountSatang % d.Debtors.Count == 0;
            calc.Add($"฿{Money.Format(d.AmountSatang)} ÷ {d.Debtors.Count} คน = {(even ? "" : "≈ ")}฿{Money.Format(shares.Max(s => s.Satang))}/คน");
        }

        var info = new List<string>();
        var warnings = new List<string>();
        if (d.Dest == "G")
        {
            info.Add($"📍 โพสต์การ์ดในกลุ่ม {d.GroupName}");
            if (d.GroupMemberCount is > 0 && d.KnownMembers < d.GroupMemberCount)
                warnings.Add($"⚠️ บอทรู้จักสมาชิก {d.KnownMembers}/{d.GroupMemberCount} คน (คนที่ยังไม่รู้จักจะไม่ถูกเรียกเก็บ)");
            if (!d.Custom && d.Debtors.Count <= 8) info.Add("👥 " + string.Join(", ", d.Debtors.Select(x => x.Name)));
        }
        else
        {
            info.Add("✉️ ส่งส่วนตัวถึง " + string.Join(", ", d.Debtors.Select(x => x.Name)));
            warnings.Add("ลูกหนี้ต้องเพิ่มบอทเป็นเพื่อนจึงจะได้รับบิลส่วนตัว");
        }
        if (merging) info.Add("📎 เพิ่มเข้าบิลเดิมที่ยังไม่ปิด");

        return Cards.Confirm(options.Value, new Cards.ConfirmInfo(d.Title, calc, info, warnings));
    }
}
