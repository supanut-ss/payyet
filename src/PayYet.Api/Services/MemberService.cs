using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayYet.Config;
using PayYet.Data;
using PayYet.Line;

namespace PayYet.Services;

/// <summary>Learns users, groups and group members from webhook events (works without a verified OA).</summary>
public class MemberService(AppDbContext db, LineClient line, IOptions<AppSettings> options, ILogger<MemberService> log)
{
    public async Task<AppUser> EnsureUserAsync(string lineUserId, string? groupLineId = null, bool? friend = null)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
        if (user == null)
        {
            var profile = groupLineId != null
                ? await line.GetGroupMemberProfileAsync(groupLineId, lineUserId)
                : await line.GetProfileAsync(lineUserId);
            user = new AppUser
            {
                LineUserId = lineUserId,
                DisplayName = profile?.DisplayName,
                PictureUrl = profile == null ? null : profile.PictureUrl ?? "",
                IsFriend = friend ?? false,
                CreatedAt = DateTime.UtcNow
            };
            db.Users.Add(user);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                // another request created the same user at the same time
                db.ChangeTracker.Clear();
                return await db.Users.FirstAsync(u => u.LineUserId == lineUserId);
            }
        }
        else
        {
            var dirty = false;
            if (friend.HasValue && user.IsFriend != friend.Value) { user.IsFriend = friend.Value; dirty = true; }
            if (string.IsNullOrEmpty(user.DisplayName) || user.PictureUrl == null)
            {
                var p = groupLineId != null
                    ? await line.GetGroupMemberProfileAsync(groupLineId, lineUserId)
                    : await line.GetProfileAsync(lineUserId);
                if (p?.DisplayName != null && string.IsNullOrEmpty(user.DisplayName)) { user.DisplayName = p.DisplayName; dirty = true; }
                if (p != null && user.PictureUrl == null) { user.PictureUrl = p.PictureUrl ?? ""; dirty = true; }
            }
            if (dirty) await db.SaveChangesAsync();
        }
        return user;
    }

    public async Task<ChatGroup> EnsureGroupAsync(string lineGroupId)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.LineGroupId == lineGroupId);
        if (group == null)
        {
            var summary = await line.GetGroupSummaryAsync(lineGroupId);
            group = new ChatGroup
            {
                LineGroupId = lineGroupId,
                Name = summary?.Name,
                PictureUrl = summary == null ? null : summary.Value.PictureUrl ?? "",
                Active = true
            };
            db.Groups.Add(group);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return await db.Groups.FirstAsync(g => g.LineGroupId == lineGroupId);
            }
        }
        else
        {
            var dirty = false;
            if (!group.Active) { group.Active = true; dirty = true; }
            if (group.PictureUrl == null)
            {
                var summary = await line.GetGroupSummaryAsync(lineGroupId);
                if (summary != null)
                {
                    group.PictureUrl = summary.Value.PictureUrl ?? "";
                    if (string.IsNullOrEmpty(group.Name)) group.Name = summary.Value.Name;
                    dirty = true;
                }
            }
            if (dirty) await db.SaveChangesAsync();
        }
        return group;
    }

    /// <summary>
    /// Fills in missing group / profile pictures for everything the collector can pick, so the picker page can show avatars.
    /// Runs only for rows that were never fetched (NULL), a few LINE calls at a time; failures just leave the avatar blank.
    /// </summary>
    public async Task BackfillPicturesAsync(long collectorId)
    {
        try
        {
            var groups = await db.GroupMembers.Where(m => m.UserId == collectorId && m.Group.Active).Select(m => m.Group).ToListAsync();

            foreach (var g in groups.Where(g => g.PictureUrl == null))
            {
                var summary = await line.GetGroupSummaryAsync(g.LineGroupId);
                if (summary == null) continue;
                g.PictureUrl = summary.Value.PictureUrl ?? "";
                if (string.IsNullOrEmpty(g.Name)) g.Name = summary.Value.Name;
            }

            var groupIds = groups.Select(g => g.Id).ToList();
            var missing = await db.GroupMembers
                .Where(m => groupIds.Contains(m.GroupId) && m.UserId != collectorId && m.User.PictureUrl == null)
                .Include(m => m.User).Include(m => m.Group).ToListAsync();
            var targets = missing.GroupBy(m => m.UserId).Select(g => g.First()).Take(80).ToList();

            using var gate = new SemaphoreSlim(6);
            var fetched = await Task.WhenAll(targets.Select(async m =>
            {
                await gate.WaitAsync();
                try { return (m.User, Profile: await line.GetGroupMemberProfileAsync(m.Group.LineGroupId, m.User.LineUserId)); }
                finally { gate.Release(); }
            }));
            foreach (var (user, profile) in fetched)
            {
                if (profile == null) continue;
                user.PictureUrl = profile.PictureUrl ?? "";
                if (string.IsNullOrEmpty(user.DisplayName)) user.DisplayName = profile.DisplayName;
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Picture backfill failed");   // avatars are decoration; never block the picker
        }
    }

    /// <summary>Records that a user is in a group (any event with a userId in the group counts).</summary>
    public async Task<(AppUser User, ChatGroup Group)> TouchAsync(string lineGroupId, string lineUserId, MemberSource source = MemberSource.SEEN)
    {
        var group = await EnsureGroupAsync(lineGroupId);
        var user = await EnsureUserAsync(lineUserId, lineGroupId);
        var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == group.Id && m.UserId == user.Id);
        var now = DateTime.UtcNow;
        if (member == null)
        {
            db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = user.Id, JoinedAt = now, Source = source, LastSeenAt = now });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
        else if (member.LastSeenAt == null || now - member.LastSeenAt > TimeSpan.FromMinutes(10))
        {
            member.LastSeenAt = now;
            await db.SaveChangesAsync();
        }
        return (user, group);
    }

    public async Task RemoveMemberAsync(string lineGroupId, string lineUserId)
    {
        var m = await db.GroupMembers.FirstOrDefaultAsync(x => x.Group.LineGroupId == lineGroupId && x.User.LineUserId == lineUserId);
        if (m != null) { db.GroupMembers.Remove(m); await db.SaveChangesAsync(); }
    }

    /// <summary>Refreshes the group's real member count (at most hourly) and, on verified OAs, lists all members.</summary>
    public async Task RefreshGroupAsync(ChatGroup group, bool listMembers = false)
    {
        if (group.MemberCountAt == null || DateTime.UtcNow - group.MemberCountAt > TimeSpan.FromHours(1))
        {
            var count = await line.GetGroupMemberCountAsync(group.LineGroupId);
            if (count.HasValue) { group.MemberCount = count; group.MemberCountAt = DateTime.UtcNow; await db.SaveChangesAsync(); }
        }

        if (listMembers && options.Value.Line.CanListGroupMembers)
        {
            try
            {
                foreach (var id in await line.GetGroupMemberIdsAsync(group.LineGroupId))
                    await TouchAsync(group.LineGroupId, id, MemberSource.LISTED);
            }
            catch (Exception ex) { log.LogWarning(ex, "Listing members of {Group} failed", group.LineGroupId); }
        }
    }
}
