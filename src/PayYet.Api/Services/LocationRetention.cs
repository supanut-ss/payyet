using Microsoft.EntityFrameworkCore;
using PayYet.Data;

namespace PayYet.Services;

public static class LocationRetention
{
    /// <summary>Deletes pings received more than <paramref name="days"/> days ago; returns how many. 0 or less keeps everything.</summary>
    public static async Task<int> PurgeAsync(AppDbContext db, int days, DateTime? nowUtc = null)
    {
        if (days <= 0) return 0;
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-days);
        return await db.LocationPings.Where(p => p.ReceivedAt < cutoff).ExecuteDeleteAsync();
    }
}
