using Microsoft.EntityFrameworkCore;
using PayYet.Data;
using PayYet.Domain;

namespace PayYet.Services;

public class DraftDebtor
{
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public long? Satang { get; set; }        // only for custom amounts
}

/// <summary>A bill the collector has typed but not yet confirmed. Stored as JSON in conversation_state.</summary>
public class Draft
{
    public string Dest { get; set; } = "G";   // G = one card in a group, P = private bills to each person
    public long? GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public int? GroupMemberCount { get; set; }
    public int KnownMembers { get; set; }
    public string Title { get; set; } = "";
    public bool PerPerson { get; set; }
    public bool Custom { get; set; }
    public long AmountSatang { get; set; }
    public List<DraftDebtor> Debtors { get; set; } = new();

    public SplitMode Mode => Custom ? SplitMode.CUSTOM : SplitMode.EQUAL;

    public List<(long UserId, long Satang)> ComputeShares()
    {
        if (Custom) return Debtors.Select(d => (d.UserId, d.Satang ?? 0)).ToList();
        if (PerPerson) return Debtors.Select(d => (d.UserId, AmountSatang)).ToList();
        var parts = SplitCalculator.Equal(AmountSatang, Debtors.Count);
        return Debtors.Select((d, i) => (d.UserId, parts[i])).ToList();
    }

    public long Total => ComputeShares().Sum(s => s.Satang);
}

/// <param name="NeedPick">The command named nobody: <c>Draft</c> only carries title/amount/mode and the collector must pick people.</param>
public record DraftResult(Draft? Draft, string? Error, List<ChatGroup>? AskGroups, bool NeedPick = false);

public record PickPerson(long Id, string Name, string? PictureUrl);
public record PickGroup(long Id, string Name, string? PictureUrl, List<PickPerson> Members);
public record PickCandidates(List<PickGroup> Groups);

public class DraftService(AppDbContext db)
{
    public const string HelpText =
        "พิมพ์เช่น\n• เก็บ ค่าอาหาร 200  (เก็บคนละ 200 แล้วเลือกคนในหน้าถัดไป)\n• หาร ค่าเครื่องดื่ม 300  (หาร 300 ตามจำนวนคนที่เลือก)\n• หาร 600 ค่าข้าว ทุกคน  หรือ  หาร 600 ยกเว้น สมชาย\n• เก็บ 200 สมชาย  (ระบุชื่อเลย ไม่ต้องเลือก)\n• เก็บ สมชาย 200 มาลี 300  (ยอดต่างกันรายคน)";

    public async Task<DraftResult> BuildAsync(AppUser collector, string text, long? forcedGroupId = null, bool alwaysPick = false)
    {
        var parsed = QuickEntryParser.Parse(text);
        if (parsed == null || parsed.IsEmpty) return Fail(HelpText);

        var myGroups = await db.GroupMembers
            .Where(m => m.UserId == collector.Id && m.Group.Active)
            .Select(m => m.Group).ToListAsync();
        if (myGroups.Count == 0)
            return Fail("บอทยังไม่รู้จักกลุ่มของคุณ\nให้ชวนบอทเข้ากลุ่ม แล้วพิมพ์อะไรก็ได้ 1 ข้อความในกลุ่ม จากนั้นลองใหม่อีกครั้ง");

        var groupIds = myGroups.Select(g => g.Id).ToList();
        var memberRows = await db.GroupMembers
            .Where(m => groupIds.Contains(m.GroupId) && m.UserId != collector.Id)
            .Include(m => m.User).ToListAsync();

        var allPeople = memberRows.Select(m => m.User).GroupBy(u => u.Id).Select(g => g.First()).ToList();

        // 1) group chosen by name or forced by a Quick Reply tap
        ChatGroup? chosen = forcedGroupId.HasValue ? myGroups.FirstOrDefault(g => g.Id == forcedGroupId) : null;
        string? groupWord = null;
        if (chosen == null)
        {
            foreach (var t in parsed.Tokens.Where(t => t.Kind == TokenKind.Word && t.Text.Length >= 2))
            {
                var gm = myGroups.Where(g => g.Name != null && g.Name.Contains(t.Text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (gm.Count == 1 && MatchPeople(t.Text, allPeople).Count == 0)
                {
                    chosen = gm[0];
                    groupWord = t.Text;
                    break;
                }
            }
        }

        var pool = chosen != null
            ? memberRows.Where(m => m.GroupId == chosen.Id).Select(m => m.User).GroupBy(u => u.Id).Select(g => g.First()).ToList()
            : allPeople;

        // 2) "name amount" pairs → custom amounts
        var tokens = parsed.Tokens;
        var used = new bool[tokens.Count];
        var pairs = new List<(AppUser User, long Satang)>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Word || tokens[i + 1].Kind != TokenKind.Number || tokens[i].Text == groupWord) continue;
            var m = MatchPeople(tokens[i].Text, pool);
            if (m.Count > 1) return Fail(Ambiguous(tokens[i].Text, m));
            if (m.Count == 1)
            {
                pairs.Add((m[0], Money.ToSatang(tokens[i + 1].Value)));
                used[i] = used[i + 1] = true;
                i++;
            }
        }

        // 3) leftovers: amount, named people, title
        EntryToken? amountToken = null;
        var named = new List<AppUser>();
        var titleWords = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (used[i]) continue;
            var t = tokens[i];
            if (t.Kind == TokenKind.Number) { amountToken ??= t; continue; }
            if (t.Text == groupWord) continue;
            var m = MatchPeople(t.Text, pool);
            if (m.Count > 1) return Fail(Ambiguous(t.Text, m));
            if (m.Count == 1) { if (named.All(u => u.Id != m[0].Id)) named.Add(m[0]); }
            else titleWords.Add(t.Text);
        }
        foreach (var (u, _) in pairs) if (named.All(n => n.Id != u.Id)) named.Add(u);

        // Without names, "ทุกคน", an exclusion list or a group name, the collector picks the people (checkbox page in LINE)
        // and the bill is split by the number of people picked.
        var wantsEveryone = parsed.Everyone || parsed.ExcludedWords.Count > 0 || groupWord != null || (forcedGroupId.HasValue && !alwaysPick);
        // alwaysPick (typed in a group): names are chosen on the picker page, like a private command; only custom "name amount" pairs skip it.
        var explicitEveryone = parsed.Everyone || parsed.ExcludedWords.Count > 0;
        if (pairs.Count == 0 && ((alwaysPick && !explicitEveryone) || (named.Count == 0 && !wantsEveryone)))
        {
            if (amountToken == null) return Fail("ไม่พบยอดเงิน ลองพิมพ์เช่น หาร ค่าเครื่องดื่ม 300 หรือ เก็บ ค่าอาหาร 200");
            var proto = new Draft
            {
                Dest = "?",
                GroupId = chosen?.Id,
                PerPerson = parsed.PerPerson,
                AmountSatang = Money.ToSatang(amountToken.Value),
                Title = titleWords.Count > 0 ? string.Join(" ", titleWords) : $"ยอดเรียกเก็บ {BangkokTime.Today:d/M}"
            };
            if (proto.AmountSatang <= 0) return Fail("ยอดเงินต้องมากกว่า 0");
            return new DraftResult(proto, null, null, NeedPick: true);
        }

        // 4) destination
        var draft = new Draft { Title = titleWords.Count > 0 ? string.Join(" ", titleWords) : $"ยอดเรียกเก็บ {BangkokTime.Today:d/M}" };
        if (chosen == null && named.Count > 0)
        {
            draft.Dest = "P";
        }
        else
        {
            draft.Dest = "G";
            chosen ??= myGroups.Count == 1 ? myGroups[0]
                : myGroups.FirstOrDefault(g => g.Id == collector.LastGroupId);
            if (chosen == null) return new DraftResult(null, null, myGroups);
            pool = memberRows.Where(m => m.GroupId == chosen.Id).Select(m => m.User).ToList();
        }

        // 5) debtors
        if (pairs.Count > 0)
        {
            draft.Custom = true;
            draft.Debtors = pairs.Select(p => new DraftDebtor { UserId = p.User.Id, Name = p.User.Name, Satang = p.Satang }).ToList();
        }
        else
        {
            if (amountToken == null) return Fail("ไม่พบยอดเงิน ลองพิมพ์เช่น เก็บ 500 ค่าข้าว");
            draft.AmountSatang = Money.ToSatang(amountToken.Value);
            draft.PerPerson = parsed.PerPerson;
            IEnumerable<AppUser> people = named.Count > 0 ? named : pool;
            if (named.Count == 0 && parsed.ExcludedWords.Count > 0)
            {
                var excludedIds = new HashSet<long>();
                foreach (var w in parsed.ExcludedWords)
                {
                    var m = MatchPeople(w, pool);
                    if (m.Count == 0) return Fail($"ไม่พบชื่อ \"{w}\" ในกลุ่ม");
                    if (m.Count > 1) return Fail(Ambiguous(w, m));
                    excludedIds.Add(m[0].Id);
                }
                people = people.Where(u => !excludedIds.Contains(u.Id));
            }
            draft.Debtors = people.Select(u => new DraftDebtor { UserId = u.Id, Name = u.Name }).ToList();
        }

        if (draft.Debtors.Count == 0)
            return Fail("ยังไม่รู้จักสมาชิกในกลุ่มนี้ ให้เพื่อนๆ พิมพ์ทักทายในกลุ่มก่อน แล้วลองใหม่");
        if (draft.ComputeShares().Any(s => s.Satang <= 0)) return Fail("ยอดเงินต้องมากกว่า 0");

        if (draft.Dest == "G" && chosen != null)
        {
            draft.GroupId = chosen.Id;
            draft.GroupName = chosen.Name ?? "(ไม่ทราบชื่อกลุ่ม)";
            draft.GroupMemberCount = chosen.MemberCount;
            draft.KnownMembers = await db.GroupMembers.CountAsync(m => m.GroupId == chosen.Id);
        }
        return new DraftResult(draft, null, null);
    }

    /// <summary>
    /// Group command that tags people ("เก็บ ค่าข้าว 200 @สมชาย @มาลี" or "... ยกเว้น @ต้น"): the tags are exact, so no picker is needed.
    /// <paramref name="text"/> has the tags already cut out. Tagged people must be members the bot knows in that group.
    /// </summary>
    public async Task<DraftResult> BuildFromTagsAsync(AppUser collector, string text, long groupId, IReadOnlyCollection<string> taggedLineUserIds, bool exclude)
    {
        var parsed = QuickEntryParser.Parse(text);
        if (parsed == null) return Fail(HelpText);
        var amountToken = parsed.Tokens.FirstOrDefault(t => t.Kind == TokenKind.Number);
        if (amountToken == null) return Fail("ไม่พบยอดเงิน ลองพิมพ์เช่น เก็บ ค่าข้าว 200 @สมชาย @มาลี");
        var amount = Money.ToSatang(amountToken.Value);
        if (amount <= 0) return Fail("ยอดเงินต้องมากกว่า 0");

        var members = await db.GroupMembers.Where(m => m.GroupId == groupId && m.UserId != collector.Id)
            .Include(m => m.User).Select(m => m.User).ToListAsync();
        var tagged = members.Where(u => taggedLineUserIds.Contains(u.LineUserId)).ToList();
        var unknown = taggedLineUserIds.Count(id => id != collector.LineUserId && members.All(u => u.LineUserId != id));
        if (unknown > 0)
            return Fail($"บอทยังไม่รู้จักคนที่แท็ก {unknown} คน ให้เขาพิมพ์ข้อความในกลุ่มก่อน แล้วสั่งใหม่");

        var debtors = exclude ? members.Where(u => tagged.All(t => t.Id != u.Id)).ToList() : tagged;
        if (debtors.Count == 0) return Fail(exclude ? "ยกเว้นแล้วไม่เหลือใครให้เก็บ" : "ไม่พบคนที่แท็ก (แท็กคนอื่นที่ไม่ใช่ตัวคุณ)");

        var proto = new Draft
        {
            Title = string.Join(" ", parsed.Tokens.Where(t => t.Kind == TokenKind.Word).Select(t => t.Text)) is { Length: > 0 } t0 ? t0 : $"ยอดเรียกเก็บ {BangkokTime.Today:d/M}",
            PerPerson = parsed.PerPerson,
            AmountSatang = amount
        };
        return await BuildFromSelectionAsync(collector, proto, debtors.Select(u => u.Id).ToList(), $"G:{groupId}");
    }

    private static string? Pic(string? url) => string.IsNullOrEmpty(url) ? null : url;

    /// <summary>Everyone the collector may bill: members of the collector's active groups.</summary>
    public async Task<PickCandidates> GetCandidatesAsync(AppUser collector)
    {
        var groups = await db.GroupMembers
            .Where(m => m.UserId == collector.Id && m.Group.Active)
            .Select(m => m.Group).ToListAsync();
        var groupIds = groups.Select(g => g.Id).ToList();
        var rows = await db.GroupMembers
            .Where(m => groupIds.Contains(m.GroupId) && m.UserId != collector.Id)
            .Include(m => m.User).ToListAsync();
        var pickGroups = groups.Select(g => new PickGroup(g.Id, g.Name ?? "(ไม่ทราบชื่อกลุ่ม)", Pic(g.PictureUrl),
                rows.Where(r => r.GroupId == g.Id).Select(r => new PickPerson(r.UserId, r.User.Name, Pic(r.User.PictureUrl)))
                    .OrderBy(p => p.Name).ToList()))
            .OrderBy(g => g.Name).ToList();
        return new PickCandidates(pickGroups);
    }

    /// <summary>
    /// Finishes a draft after the collector picked people on the picker page.
    /// Only people the collector may bill are accepted - never trust ids sent by the browser.
    /// </summary>
    /// <param name="dest">"P" = private bills, "G:&lt;groupId&gt;" = one card in that group (everyone picked must be in it).</param>
    public async Task<DraftResult> BuildFromSelectionAsync(AppUser collector, Draft proto, IReadOnlyCollection<long> userIds, string? dest, IReadOnlyDictionary<long, decimal>? amounts = null)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return Fail("ยังไม่ได้เลือกใคร");

        var cand = await GetCandidatesAsync(collector);
        var allowed = cand.Groups.SelectMany(g => g.Members)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        if (ids.Any(id => !allowed.ContainsKey(id))) return Fail("เลือกได้เฉพาะคนในกลุ่มของคุณ");

        var draft = new Draft
        {
            Title = proto.Title,
            PerPerson = proto.PerPerson,
            AmountSatang = proto.AmountSatang,
            Debtors = ids.Select(id => new DraftDebtor { UserId = id, Name = allowed[id].Name }).ToList()
        };

        // Per-person amounts edited on the page (split or each-pays): the bill becomes a custom one.
        if (amounts is { Count: > 0 })
        {
            if (ids.Any(id => !amounts.ContainsKey(id))) return Fail("ใส่ยอดให้ครบทุกคนที่เลือก");
            if (ids.Any(id => amounts[id] <= 0 || amounts[id] > 1_000_000m)) return Fail("ยอดของแต่ละคนต้องมากกว่า 0 และไม่เกิน 1,000,000 บาท");
            draft.Custom = true;
            foreach (var d in draft.Debtors) d.Satang = Money.ToSatang(amounts[d.UserId]);
            draft.AmountSatang = draft.Debtors.Sum(d => d.Satang ?? 0);
        }

        if (dest != null && dest.StartsWith("G:", StringComparison.Ordinal) && long.TryParse(dest[2..], out var groupId))
        {
            var group = cand.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group == null) return Fail("ไม่พบกลุ่มนี้");
            var members = group.Members.Select(m => m.Id).ToHashSet();
            if (ids.Any(id => !members.Contains(id))) return Fail("ทุกคนที่เลือกต้องอยู่ในกลุ่มที่จะแจ้งบิล");

            var entity = await db.Groups.FindAsync(groupId);
            draft.Dest = "G";
            draft.GroupId = groupId;
            draft.GroupName = group.Name;
            draft.GroupMemberCount = entity?.MemberCount;
            draft.KnownMembers = await db.GroupMembers.CountAsync(m => m.GroupId == groupId);
        }
        else draft.Dest = "P";

        if (draft.ComputeShares().Any(s => s.Satang <= 0)) return Fail("ยอดเงินต้องมากกว่า 0");
        return new DraftResult(draft, null, null);
    }

    /// <summary>People whose display name contains the word; an exact match wins over partial matches.</summary>
    internal static List<AppUser> MatchPeople(string word, IEnumerable<AppUser> pool)
    {
        if (word.Length < 2) return new List<AppUser>();
        var list = pool.Where(u => u.DisplayName != null && u.DisplayName.Contains(word, StringComparison.OrdinalIgnoreCase)).ToList();
        var exact = list.Where(u => string.Equals(u.DisplayName, word, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : list;
    }

    private static string Ambiguous(string word, List<AppUser> matches) =>
        $"\"{word}\" ตรงกับหลายคน: {string.Join(", ", matches.Take(5).Select(u => u.Name))}\nพิมพ์ชื่อให้ยาวขึ้นอีกนิดแล้วลองใหม่";

    private static DraftResult Fail(string message) => new(null, message, null);
}
