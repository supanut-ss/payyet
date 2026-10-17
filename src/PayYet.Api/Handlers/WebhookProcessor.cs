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
                var tags = new List<string>();
                if (text != null) text = StripMentions(text, Prop(msg, "mention"), tags);
                if (srcType == "group" && groupId != null) await OnGroupMessageAsync(groupId, userId, replyToken, text, tags);
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
                "สั่งเก็บเงินได้เลย เช่น หาร ค่าข้าว 500 หรือ เก็บ ค่าข้าว 100 (ต้องตั้งพร้อมเพย์กับบอทก่อน)"));
    }

    private async Task OnGroupMessageAsync(string groupId, string userId, string? replyToken, string? text, List<string> tags)
    {
        var (me, group) = await members.TouchAsync(groupId, userId);
        if (text == null || replyToken == null) return;
        text = text.Trim();

        if (IsHelp(text))
        {
            await line.ReplyAsync(replyToken, Msg.Text(GroupHelp));
        }
        else if (text is "ยอด" or "ยอดค้าง")
        {
            await ReplyWithTagAsync(replyToken, await GroupStatusAsync(group, me), "ยอดค้างของ", me);
        }
        else if (Regex.IsMatch(text, @"^(เก็บ(เงิน)?|หาร(เท่า)?)\s+.*[\d๐-๙]"))
        {
            await StartGroupCreateAsync(me, group, replyToken, text, tags);
        }
    }

    private static bool IsHelp(string text) =>
        text.Equals("payyet", StringComparison.OrdinalIgnoreCase) || text.Equals("help", StringComparison.OrdinalIgnoreCase) || text == "วิธีใช้";

    private const string GroupHelp =
        "📖 PayYet วิธีใช้ในกลุ่ม" + "\n" +
        "• เก็บ ค่าข้าว 200 → เก็บคนละ 200 แล้วเลือกคน" + "\n" +
        "• หาร ค่าเครื่องดื่ม 300 → หารยอดรวมตามคนที่เลือก" + "\n" +
        "• เก็บ ค่าข้าว 200 ทุกคน → เก็บทุกคนในกลุ่มทันที" + "\n" +
        "• หาร 300 ทุกคน ยกเว้น ต้น → ทุกคนยกเว้นชื่อที่พิมพ์" + "\n" +
        "• เก็บ ค่าข้าว 200 @คน @คน → เก็บเฉพาะคนที่แท็ก" + "\n" +
        "• หาร 300 ยกเว้น @คน → ทุกคนยกเว้นคนที่แท็ก" + "\n" +
        "• ยอด → ดูยอดค้างของตัวเอง" + "\n" +
        "ต้องตั้งพร้อมเพย์ก่อนเก็บเงิน · พิมพ์ help ในแชทส่วนตัวกับบอทเพื่อดูแบบละเอียด";

    private string PrivateHelp()
    {
        var web = options.Value.App.WebBaseUrl.TrimEnd('/');
        return
            "📖 PayYet วิธีใช้" + "\n\n" +
            "ตั้งค่าครั้งแรก" + "\n" +
            "• พร้อมเพย์ 0812345678 → ตั้งเลขรับเงิน (หรือกดปุ่มพร้อมเพย์ในเมนู)" + "\n" +
            "• ชวนบอทเข้ากลุ่ม แล้วให้ทุกคนพิมพ์ข้อความ 1 ครั้ง บอทจะรู้จักสมาชิก" + "\n\n" +
            "สั่งเก็บเงิน (ในกลุ่มหรือแชทนี้)" + "\n" +
            "• เก็บ ค่าข้าว 200 → เก็บคนละ 200 แล้วเลือกคน" + "\n" +
            "• หาร ค่าเครื่องดื่ม 300 → หารยอดรวมตามคนที่เลือก แก้ยอดรายคนได้" + "\n" +
            "• เก็บ ค่าข้าว 200 ทุกคน / หาร 300 ทุกคน ยกเว้น ต้น" + "\n" +
            "• เก็บ ค่าข้าว สมชาย 100 มาลี 150 → ระบุยอดรายคน" + "\n" +
            "• ในกลุ่ม: แท็กชื่อ เช่น เก็บ ค่าข้าว 200 @สมชาย หรือ หาร 300 ยกเว้น @ต้น" + "\n\n" +
            "ดูและจัดการ" + "\n" +
            "• ยอด → ที่ฉันต้องจ่าย / ที่คนอื่นค้างฉัน" + "\n" +
            "• จัดการ → รับเงินสด ยกหนี้ ยกเลิกรายการหรือบิล" + "\n" +
            "• ผู้จ่ายกด \"จ่ายเงิน\" บนการ์ด สแกน QR แล้วแนบสลิป ผู้เก็บกดยืนยัน" + "\n" +
            "• บอทเตือนคนที่ยังไม่จ่ายทุกวัน 10:00" + "\n\n" +
            $"รายละเอียดทั้งหมด: {web}/" + "\n" +
            $"เปิดแอป: https://liff.line.me/{options.Value.Liff.Id}?mode=help";
    }

    // A card posted in a group is followed by a tag of the person it is about.
    private async Task ReplyWithTagAsync(string replyToken, object card, string prefix, AppUser who)
    {
        var tag = Msg.Mention(prefix, new[] { who.LineUserId });
        if (tag == null) await line.ReplyAsync(replyToken, card);
        else await line.ReplyAsync(replyToken, card, tag);
    }

    // "เก็บ/หาร ..." typed in a group: a pick card is posted in the group, the collector picks people (and can edit each amount) on the page,
    // Needs the collector's PromptPay first.
    private async Task StartGroupCreateAsync(AppUser me, ChatGroup group, string replyToken, string text, List<string> tags)
    {
        var liffBase = $"https://liff.line.me/{options.Value.Liff.Id}";
        if (string.IsNullOrEmpty(me.PromptPayId))
        {
            await line.ReplyAsync(replyToken, Msg.Text($"{me.Name} ยังไม่ได้ตั้งพร้อมเพย์ ⚠️\nตั้งก่อนเก็บเงินได้ที่ {liffBase}?mode=promptpay\nหรือพิมพ์ในแชทส่วนตัวกับบอท: พร้อมเพย์ 0812345678"));
            return;
        }

        // Tagged people are exact: create the bill right away ("ยกเว้น" = everyone else in the group).
        if (tags.Count > 0)
        {
            var exclude = Regex.IsMatch(text, @"(^|\s)ยกเว้น(\s|$)");
            var tagged = await drafts.BuildFromTagsAsync(me, text, group.Id, tags, exclude);
            if (tagged.Error != null || tagged.Draft == null)
            {
                await line.ReplyAsync(replyToken, Msg.Text(tagged.Error ?? "สั่งเก็บไม่สำเร็จ ลองใหม่อีกครั้ง"));
                return;
            }
            var made = await creation.CreateAsync(me, tagged.Draft);
            await line.PushAsync(me.LineUserId, Cards.Created(options.Value, "สร้างบิลแล้ว", made.Summary,
                made.Undo.Select(u => (u.Label, $"act=undo&c={u.ChargeId}"))));
            return;
        }

        var result = await drafts.BuildAsync(me, text, group.Id, alwaysPick: true);
        if (result.Error != null || result.Draft == null)
        {
            await line.ReplyAsync(replyToken, Msg.Text(result.Error ?? "สั่งเก็บไม่สำเร็จ ลองใหม่อีกครั้ง"));
            return;
        }

        var draft = result.Draft;
        if (result.NeedPick)
        {
            // The card is posted in the group; only the collector's own state can be used on the page, so others tapping it get "expired".
            await SetStateAsync(me.Id, "PICK", "pick", draft);
            var what = draft.PerPerson ? $"เก็บคนละ ฿{Money.Format(draft.AmountSatang)}" : $"หาร ฿{Money.Format(draft.AmountSatang)}";
            await ReplyWithTagAsync(replyToken, Cards.PickPrompt(options.Value, draft.Title, what, $"{liffBase}?mode=pick", me.Name, allowCancel: false), "เลือกคนที่จะเก็บเงิน", me);
            return;
        }

        // "ทุกคน" / "ยกเว้น ..." for the whole group: nothing left to choose, so create it right away.
        if (draft.Dest == "G" && !draft.Custom)
        {
            var made = await creation.CreateAsync(me, draft);
            await line.PushAsync(me.LineUserId, Cards.Created(options.Value, "สร้างบิลแล้ว", made.Summary,
                made.Undo.Select(u => (u.Label, $"act=undo&c={u.ChargeId}"))));
            return;
        }

        await SetStateAsync(me.Id, "CONFIRM", "confirm", draft);
        if (await line.PushAsync(me.LineUserId, await ConfirmCardAsync(me, draft)))
            await line.ReplyAsync(replyToken, Msg.Text($"{me.Name} ส่งการ์ดยืนยันไปที่แชทส่วนตัวกับบอทแล้ว 📩"));
        else
            await line.ReplyAsync(replyToken, Msg.Text("บอทส่งข้อความหาคุณไม่ได้ กรุณาเพิ่มบอทเป็นเพื่อนก่อน แล้วสั่งใหม่อีกครั้ง"));
    }

    // Only the asker's own unpaid amounts in this group; other people's balances are never posted to the group.
    private async Task<object> GroupStatusAsync(ChatGroup group, AppUser me)
    {
        var mine = await db.BillItems.Include(i => i.Bill).ThenInclude(b => b.Creator)
            .Where(i => i.DebtorId == me.Id && i.Bill.GroupId == group.Id && i.Bill.Status == BillStatus.OPEN)
            .OrderBy(i => i.BillId).ToListAsync();
        var open = mine.Where(i => !i.IsSettled).ToList();
        var oweUrl = $"https://liff.line.me/{options.Value.Liff.Id}?mode=owe";
        if (open.Count == 0) return Cards.MyBalance(options.Value, me.Name, Array.Empty<Cards.OwedLine>(), oweUrl);

        var lines = open.Select(i => new Cards.OwedBill(i.Bill.Title, "เก็บโดย " + i.Bill.Creator.Name, 0, 0, i.Remaining,
            $"https://liff.line.me/{options.Value.Liff.Id}?b={i.Bill.Token}", i.Status == ItemStatus.SUBMITTED)).ToList();
        return Cards.Outstanding(options.Value, $"ยอดค้างของ {me.Name}", lines, Array.Empty<Cards.OwingPerson>(), oweUrl, "💳 ที่ต้องจ่าย", accent: Theme.Orange);
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

        if (IsHelp(text))
        {
            await line.ReplyAsync(replyToken, Msg.Text(PrivateHelp()));
            return;
        }

        if (text is "ยอด" or "ยอดค้าง")
        {
            await line.ReplyAsync(replyToken, await PrivateStatusAsync(user));
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

    /// <summary>"ยอด" in the private chat: what I owe, and what others owe me, each as a bill-by-bill card.</summary>
    private async Task<object[]> PrivateStatusAsync(AppUser user)
    {
        var liff = $"https://liff.line.me/{options.Value.Liff.Id}";
        var messages = new List<object>();

        var owe = await db.BillItems.Include(i => i.Bill).ThenInclude(b => b.Creator).Include(i => i.Bill).ThenInclude(b => b.Group)
            .Where(i => i.DebtorId == user.Id && i.Bill.Status == BillStatus.OPEN).OrderBy(i => i.BillId).ToListAsync();
        owe = owe.Where(i => !i.IsSettled).ToList();
        if (owe.Count > 0)
        {
            var lines = owe.Select(i => new Cards.OwedBill(i.Bill.Title, "เก็บโดย " + i.Bill.Creator.Name + (i.Bill.Group?.Name != null ? " · " + i.Bill.Group.Name : ""), 0, 0,
                i.Remaining, $"{liff}?b={i.Bill.Token}", i.Status == ItemStatus.SUBMITTED)).ToList();
            messages.Add(Cards.Outstanding(options.Value, "ยอดของคุณ", lines, Array.Empty<Cards.OwingPerson>(), $"{liff}?mode=owe", "💳 ที่ต้องจ่าย", accent: Theme.Orange));
        }

        var mine = await db.Bills.Include(b => b.Group).Include(b => b.Items).ThenInclude(i => i.Debtor)
            .Where(b => b.CreatorId == user.Id && b.Status == BillStatus.OPEN).OrderBy(b => b.Id).ToListAsync();
        var collecting = mine.Where(b => b.Items.Any(i => !i.IsSettled)).ToList();
        if (collecting.Count > 0)
        {
            var lines = collecting.Select(b =>
            {
                var unpaid = b.Items.Where(i => !i.IsSettled).ToList();
                return new Cards.OwedBill(b.Title, b.Group?.Name != null ? "กลุ่ม " + b.Group.Name : "ส่วนตัว", unpaid.Count, b.Items.Count(i => i.Status != ItemStatus.WAIVED),
                    unpaid.Where(i => i.Status != ItemStatus.SUBMITTED).Sum(i => i.Remaining), $"{liff}?mode=bill&id={b.Id}", unpaid.All(i => i.Status == ItemStatus.SUBMITTED));
            }).ToList();
            var people = collecting.SelectMany(b => b.Items.Where(i => !i.IsSettled && i.Status != ItemStatus.SUBMITTED && i.Debtor != null))
                .GroupBy(i => i.DebtorId).Select(g => new Cards.OwingPerson(g.First().Debtor!.Name, g.Count(), g.Sum(i => i.Remaining)))
                .OrderByDescending(p => p.RemainingSatang).ToList();
            messages.Add(Cards.Outstanding(options.Value, "บิลที่คุณเก็บ", lines, people, $"{liff}?mode=manage", "📥 คนอื่นค้างคุณ", "จัดการบิล", "ใครค้างบ้าง", Theme.Green));
        }

        if (messages.Count == 0) messages.Add(Msg.Text("ตอนนี้ไม่มียอดค้าง 🎉 ไม่มีบิลที่คุณต้องจ่าย และไม่มีคนค้างคุณ"));
        return messages.Take(5).ToArray();
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

    /// <summary>Removes "@name" mentions from a user's text (so the parser sees plain words) and collects the tagged user ids.</summary>
    private static string StripMentions(string text, JsonElement mention, List<string> tagged)
    {
        var arr = Prop(mention, "mentionees");
        if (arr.ValueKind != JsonValueKind.Array) return text;
        var ranges = new List<(int Index, int Length)>();
        foreach (var m in arr.EnumerateArray())
        {
            if (Str(m, "type") != "user" || Str(m, "userId") is not { } uid) continue;
            tagged.Add(uid);
            if (Prop(m, "index").TryGetInt32(out var idx) && Prop(m, "length").TryGetInt32(out var len) && idx >= 0 && len > 0 && idx + len <= text.Length)
                ranges.Add((idx, len));
        }
        foreach (var (idx, len) in ranges.OrderByDescending(r => r.Index))
            text = text.Remove(idx, len);
        return text.Trim();
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
