using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Domain;
using PayYet.Line;
using PayYet.Services;

namespace PayYet.Handlers;

/// <summary>Routes LINE webhook events: lifecycle, group chatter, private-chat commands and postbacks.</summary>
public class WebhookProcessor(
    AppDbContext db, LineClient line, MemberService members, DraftService drafts,
    PaymentService payments, DraftCardBuilder draftCards, BillCreationService creation, IOptions<AppSettings> options,
    ILogger<WebhookProcessor> log)
{
    private const string Welcome =
        "สวัสดีครับ 👋 ผมช่วยเก็บเงินในกลุ่มหรือรายคน\n" +
        "1) ตั้งเลขพร้อมเพย์รับเงิน: พร้อมเพย์ 0812345678\n" +
        "2) สั่งเก็บเงินที่นี่ (แชทส่วนตัว) เช่น หาร ค่าข้าว 500 หรือ เก็บ ค่าอาหาร 200\n" +
        "3) เช็คยอด: พิมพ์ ยอด  |  จัดการบิล (รับเงินสด ยกหนี้ ยกเลิก): พิมพ์ จัดการ\n\n" + DraftService.HelpText;

    public async Task HandleAsync(JsonElement ev)
    {
        var type = Str(ev, "type");
        var src = Prop(ev, "source");
        var srcType = Str(src, "type");
        var userId = Str(src, "userId");
        var groupId = Str(src, "groupId");
        var replyToken = Str(ev, "replyToken");

        var eventId = Str(ev, "webhookEventId");
        if (eventId != null && !await MarkProcessedAsync(eventId)) return;
        if (srcType == "room") return;

        switch (type)
        {
            case "follow" when userId != null:
                await members.EnsureUserAsync(userId, friend: true);
                if (replyToken != null) await line.ReplyAsync(replyToken, Msg.Text(Welcome));
                break;

            case "unfollow" when userId != null:
                await members.EnsureUserAsync(userId, friend: false);
                break;

            case "join" when groupId != null:
                await OnJoinAsync(groupId, userId, replyToken);
                break;

            case "leave" when groupId != null:
                var left = await db.Groups.FirstOrDefaultAsync(g => g.LineGroupId == groupId);
                if (left != null) { left.Active = false; await db.SaveChangesAsync(); }
                break;

            case "memberJoined" when groupId != null:
                foreach (var m in Members(ev, "joined"))
                    if (Str(m, "userId") is { } uid) await members.TouchAsync(groupId, uid, MemberSource.JOINED);
                var jg = await members.EnsureGroupAsync(groupId);
                jg.MemberCountAt = null;   // force a count refresh
                await members.RefreshGroupAsync(jg);
                break;

            case "memberLeft" when groupId != null:
                foreach (var m in Members(ev, "left"))
                    if (Str(m, "userId") is { } uid) await members.RemoveMemberAsync(groupId, uid);
                break;

            case "message" when userId != null:
                var msg = Prop(ev, "message");
                var text = Str(msg, "type") == "text" ? Str(msg, "text") : null;
                if (srcType == "group" && groupId != null) await OnGroupMessageAsync(groupId, userId, replyToken, text);
                else if (srcType == "user") await OnUserMessageAsync(userId, replyToken, text);
                break;

            case "postback" when userId != null:
                var data = Str(Prop(ev, "postback"), "data") ?? "";
                if (srcType == "group" && groupId != null) await members.TouchAsync(groupId, userId);
                else if (srcType == "user") await OnPostbackAsync(userId, replyToken, HttpUtility.ParseQueryString(data));
                break;
        }
    }

    // ───────────────────────── group ─────────────────────────

    private async Task OnJoinAsync(string groupId, string? inviterId, string? replyToken)
    {
        var group = await members.EnsureGroupAsync(groupId);
        if (inviterId != null) await members.TouchAsync(groupId, inviterId, MemberSource.JOINED);
        await members.RefreshGroupAsync(group, listMembers: true);
        if (replyToken != null)
            await line.ReplyAsync(replyToken, Msg.Text(
                "สวัสดีครับ 👋 ผมช่วยเก็บเงินในกลุ่มนี้\n" +
                "ขอให้ทุกคนพิมพ์อะไรก็ได้ 1 ข้อความ เพื่อให้บอทรู้จัก (ไม่ต้องเพิ่มเพื่อนบอท)\n" +
                "คนเก็บเงินสั่งเก็บได้ที่แชทส่วนตัวกับบอท"));
    }

    private async Task OnGroupMessageAsync(string groupId, string userId, string? replyToken, string? text)
    {
        var (_, group) = await members.TouchAsync(groupId, userId);
        if (text == null || replyToken == null) return;
        text = text.Trim();

        if (text is "ยอด" or "ยอดค้าง")
        {
            await line.ReplyAsync(replyToken, Msg.Text(await GroupStatusAsync(group)));
        }
        else if (Regex.IsMatch(text, @"^(เก็บ(เงิน)?|หาร(เท่า)?)\s+.*[\d๐-๙]"))
        {
            await line.ReplyAsync(replyToken, Msg.Text("สร้างบิลได้ที่แชทส่วนตัวกับบอทเท่านั้นนะครับ (เพื่อไม่ให้กลุ่มรก)"));
        }
    }

    private async Task<string> GroupStatusAsync(ChatGroup group)
    {
        var open = await db.Bills.Include(b => b.Creator)
            .Where(b => b.GroupId == group.Id && b.Status == BillStatus.OPEN).ToListAsync();
        if (open.Count == 0) return "ตอนนี้ไม่มีบิลค้างในกลุ่มนี้ 🎉";

        var sb = new System.Text.StringBuilder();
        foreach (var bill in open)
        {
            var items = await db.BillItems.Include(i => i.Debtor).Where(i => i.BillId == bill.Id).ToListAsync();
            var paid = items.Count(i => i.IsSettled);
            sb.AppendLine($"💰 บิลของ {bill.Creator.Name} (จ่ายแล้ว {paid}/{items.Count})");
            foreach (var i in items.Where(i => !i.IsSettled))
                sb.AppendLine($"  • {i.Debtor?.Name}: ฿{Money.Format(i.Remaining)}{(i.Status == ItemStatus.SUBMITTED ? " ⏳ รอตรวจ" : "")}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────── private chat ─────────────────────────

    private async Task OnUserMessageAsync(string lineUserId, string? replyToken, string? text)
    {
        var user = await members.EnsureUserAsync(lineUserId, friend: true);
        if (replyToken == null) return;
        if (text == null)
        {
            await line.ReplyAsync(replyToken, Msg.Text("แนบสลิปได้ที่ปุ่ม \"จ่ายเงิน\" บนการ์ดบิลนะครับ"));
            return;
        }
        text = text.Trim();

        var promptPay = Regex.Match(text, @"^พร้อมเพย์\s*(?<id>.*)$");
        if (promptPay.Success)
        {
            await SetPromptPayAsync(user, replyToken, promptPay.Groups["id"].Value);
            return;
        }

        if (text is "ยอด" or "ยอดค้าง")
        {
            await line.ReplyAsync(replyToken, Msg.Text(await PrivateStatusAsync(user)));
            return;
        }

        if (text is "จัดการ" or "จัดการบิล" or "บิลของฉัน")
        {
            var open = await db.Bills.Where(b => b.CreatorId == user.Id && b.Status == BillStatus.OPEN).Select(b => b.Id).ToListAsync();
            if (open.Count == 0)
            {
                await line.ReplyAsync(replyToken, Msg.Text("ตอนนี้ไม่มีบิลที่ยังเปิดอยู่ สั่งเก็บเงินใหม่ได้เลย เช่น หาร ค่าข้าว 500"));
                return;
            }
            var items = await db.BillItems.Where(i => open.Contains(i.BillId)).ToListAsync();
            var unpaid = items.Where(i => !i.IsSettled).Sum(i => i.Remaining);
            await line.ReplyAsync(replyToken, Cards.ManagePrompt(options.Value, open.Count, unpaid,
                $"https://liff.line.me/{options.Value.Liff.Id}?mode=manage"));
            return;
        }

        var state = await GetStateAsync(user.Id);
        var parsed = QuickEntryParser.Parse(text);
        if (parsed == null && state?.Flow == "ASK_AMOUNT")
        {
            text = "เก็บ " + text;
            parsed = QuickEntryParser.Parse(text);
        }

        if (parsed == null)
        {
            await line.ReplyAsync(replyToken, Msg.Text(Welcome));
            return;
        }
        if (parsed.IsEmpty)
        {
            await SetStateAsync(user.Id, "ASK_AMOUNT", "amount", null);
            await line.ReplyAsync(replyToken, Msg.Text("ยอดเท่าไหร่ครับ? (เช่น 500 ค่าข้าว)"));
            return;
        }

        await StartCreateAsync(user, replyToken, text, null);
    }

    private async Task SetPromptPayAsync(AppUser user, string replyToken, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            await line.ReplyAsync(replyToken, Msg.Text(user.PromptPayId == null
                ? "ยังไม่ได้ตั้งพร้อมเพย์ พิมพ์: พร้อมเพย์ 0812345678"
                : $"พร้อมเพย์ปัจจุบัน: {user.PromptPayId}\nเปลี่ยนได้โดยพิมพ์: พร้อมเพย์ <เบอร์/เลขบัตร>"));
            return;
        }
        if (!PromptPay.IsValidId(id))
        {
            await line.ReplyAsync(replyToken, Msg.Text("รูปแบบไม่ถูกต้อง ใช้เบอร์มือถือ 10 หลัก หรือเลขบัตรประชาชน 13 หลัก"));
            return;
        }
        user.PromptPayId = new string(id.Where(char.IsDigit).ToArray());
        await db.SaveChangesAsync();
        await line.ReplyAsync(replyToken, Msg.Text($"บันทึกพร้อมเพย์ {user.PromptPayId} แล้ว ✅\nสั่งเก็บเงินได้เลย เช่น เก็บ 500 ค่าข้าว"));
    }

    private async Task StartCreateAsync(AppUser user, string replyToken, string text, long? forcedGroupId)
    {
        if (string.IsNullOrEmpty(user.PromptPayId))
        {
            await line.ReplyAsync(replyToken, Msg.Text("ก่อนเก็บเงิน ตั้งเลขพร้อมเพย์สำหรับรับเงินก่อนนะครับ\nพิมพ์: พร้อมเพย์ 0812345678"));
            return;
        }

        var result = await drafts.BuildAsync(user, text, forcedGroupId);
        if (result.Error != null)
        {
            await line.ReplyAsync(replyToken, Msg.Text(result.Error));
            return;
        }
        if (result.AskGroups != null)
        {
            await SetStateAsync(user.Id, "GROUP_PICK", "group", new { Text = text });
            await line.ReplyAsync(replyToken, Msg.TextWithPostbackChoices("เก็บในกลุ่มไหนครับ?",
                result.AskGroups.Select(g => (g.Name ?? "(ไม่ทราบชื่อ)", $"act=pickgroup&g={g.Id}"))));
            return;
        }

        if (result.NeedPick)
        {
            // Nobody was named: the collector ticks people on a LIFF page; the confirm card follows in this chat.
            var proto = result.Draft!;
            await SetStateAsync(user.Id, "PICK", "pick", proto);
            var what = proto.PerPerson ? $"เก็บคนละ ฿{Money.Format(proto.AmountSatang)}" : $"หาร ฿{Money.Format(proto.AmountSatang)}";
            await line.ReplyAsync(replyToken, Cards.PickPrompt(options.Value, proto.Title, what,
                $"https://liff.line.me/{options.Value.Liff.Id}?mode=pick"));
            return;
        }

        var draft = result.Draft!;
        await SetStateAsync(user.Id, "CONFIRM", "confirm", draft);
        await line.ReplyAsync(replyToken, await ConfirmCardAsync(user, draft));
    }

    private Task<object> ConfirmCardAsync(AppUser user, Draft d) => draftCards.BuildAsync(user, d);

    private async Task<string> PrivateStatusAsync(AppUser user)
    {
        var sb = new System.Text.StringBuilder();

        var owe = await db.BillItems.Include(i => i.Bill).ThenInclude(b => b.Creator)
            .Where(i => i.DebtorId == user.Id && i.Bill.Status == BillStatus.OPEN).ToListAsync();
        owe = owe.Where(i => !i.IsSettled).ToList();
        sb.AppendLine("💳 ที่คุณต้องจ่าย");
        if (owe.Count == 0) sb.AppendLine("  – ไม่มี");
        foreach (var g in owe.GroupBy(i => i.Bill.Creator.Name))
            sb.AppendLine($"  • {g.Key}: ฿{Money.Format(g.Sum(i => i.Remaining))}");

        var mine = await db.Bills.Include(b => b.Group).Where(b => b.CreatorId == user.Id && b.Status == BillStatus.OPEN).ToListAsync();
        sb.AppendLine().AppendLine("📥 ที่คนอื่นค้างคุณ");
        var any = false;
        foreach (var bill in mine)
        {
            var items = await db.BillItems.Include(i => i.Debtor).Where(i => i.BillId == bill.Id).ToListAsync();
            var unpaid = items.Where(i => !i.IsSettled).ToList();
            if (unpaid.Count == 0) continue;
            any = true;
            sb.AppendLine($"  {(bill.Group?.Name != null ? "กลุ่ม " + bill.Group.Name : "ส่วนตัว")}:");
            foreach (var i in unpaid)
                sb.AppendLine($"   – {i.Debtor?.Name}: ฿{Money.Format(i.Remaining)}{(i.Status == ItemStatus.SUBMITTED ? " ⏳" : "")}");
        }
        if (!any) sb.AppendLine("  – ไม่มี");
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────── postbacks ─────────────────────────

    private async Task OnPostbackAsync(string lineUserId, string? replyToken, System.Collections.Specialized.NameValueCollection q)
    {
        var user = await members.EnsureUserAsync(lineUserId, friend: true);
        if (replyToken == null) return;

        switch (q["act"])
        {
            case "pickgroup" when long.TryParse(q["g"], out var gid):
                var st = await GetStateAsync(user.Id);
                if (st?.Flow != "GROUP_PICK") { await Expired(replyToken); return; }
                var text = JsonSerializer.Deserialize<JsonElement>(st.Data!).GetProperty("Text").GetString()!;
                await StartCreateAsync(user, replyToken, text, gid);
                break;

            case "cancel":
                await ClearStateAsync(user.Id);
                await line.ReplyAsync(replyToken, Msg.Text("ยกเลิกแล้วครับ"));
                break;

            case "confirm":
                await ConfirmDraftAsync(user, replyToken);
                break;

            case "undo" when long.TryParse(q["c"], out var cid):
                await UndoAsync(user, replyToken, cid);
                break;

            case "approve" when long.TryParse(q["p"], out var pid):
                await line.ReplyAsync(replyToken, Msg.Text(await payments.ApproveAsync(pid, lineUserId)));
                break;

            case "reject" when long.TryParse(q["p"], out var rid):
                await line.ReplyAsync(replyToken, Msg.Text(await payments.RejectAsync(rid, lineUserId)));
                break;
        }
    }

    private Task Expired(string replyToken) =>
        line.ReplyAsync(replyToken, Msg.Text("หมดเวลาแล้วครับ พิมพ์คำสั่งเก็บเงินใหม่อีกครั้ง"));

    private async Task ConfirmDraftAsync(AppUser user, string replyToken)
    {
        var st = await GetStateAsync(user.Id);
        if (st?.Flow != "CONFIRM" || st.Data == null) { await Expired(replyToken); return; }
        var d = JsonSerializer.Deserialize<Draft>(st.Data)!;
        await ClearStateAsync(user.Id);   // clear first so a double tap cannot create the charge twice

        var created = await creation.CreateAsync(user, d);
        await line.ReplyAsync(replyToken, Cards.Created(options.Value, "สร้างบิลแล้ว", created.Summary,
            created.Undo.Select(u => (u.Label, $"act=undo&c={u.ChargeId}"))));
    }

    private async Task UndoAsync(AppUser user, string replyToken, long chargeId)
    {
        var (ok, message) = await creation.UndoAsync(user, chargeId);
        await line.ReplyAsync(replyToken, Msg.Text(ok ? "↩️ " + message : "⚠️ " + message));
    }

    // ───────────────────────── helpers ─────────────────────────

    private async Task<bool> MarkProcessedAsync(string eventId)
    {
        db.ProcessedEvents.Add(new ProcessedEvent { WebhookEventId = eventId, ProcessedAt = DateTime.UtcNow });
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            log.LogInformation("Duplicate webhook event {Id} ignored", eventId);
            return false;
        }
    }

    private async Task<ConversationState?> GetStateAsync(long userId)
    {
        var s = await db.States.FindAsync(userId);
        if (s != null && s.ExpiresAt < DateTime.UtcNow)
        {
            db.States.Remove(s);
            await db.SaveChangesAsync();
            return null;
        }
        return s;
    }

    private async Task SetStateAsync(long userId, string flow, string step, object? data)
    {
        var s = await db.States.FindAsync(userId);
        if (s == null) { s = new ConversationState { UserId = userId }; db.States.Add(s); }
        s.Flow = flow;
        s.Step = step;
        s.Data = data == null ? null : JsonSerializer.Serialize(data);
        s.ExpiresAt = DateTime.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync();
    }

    private async Task ClearStateAsync(long userId)
    {
        var s = await db.States.FindAsync(userId);
        if (s != null) { db.States.Remove(s); await db.SaveChangesAsync(); }
    }

    private static List<JsonElement> Members(JsonElement ev, string key)
    {
        var holder = Prop(ev, key);
        var arr = Prop(holder, "members");
        return arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().ToList() : new List<JsonElement>();
    }

    private static JsonElement Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement el, string name)
    {
        var v = Prop(el, name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
