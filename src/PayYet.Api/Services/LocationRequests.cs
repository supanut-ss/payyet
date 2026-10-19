using System.Collections.Concurrent;

namespace PayYet.Services;

/// <summary>
/// "Send me your position now" requests for OwnTracks phones. A phone only talks to us when it posts, so a request waits
/// here and is handed over in the response of that person's next post. Kept in memory: a restart of the app just drops it.
/// </summary>
public class LocationRequests
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);

    // Housekeeping gate: the purge of old pings runs from /latest (page loads), at most once per interval.
    private long _lastPurgeTicks;
    public bool PurgeDue(TimeSpan every)
    {
        var now = DateTime.UtcNow.Ticks; var last = Interlocked.Read(ref _lastPurgeTicks);
        return now - last >= every.Ticks && Interlocked.CompareExchange(ref _lastPurgeTicks, now, last) == last;
    }

    public void Request(string name) => _pending[name] = DateTime.UtcNow;

    public bool IsPending(string name) => _pending.TryGetValue(name, out var at) && DateTime.UtcNow - at <= Lifetime;

    /// <summary>True once per request: the phone is being told to report.</summary>
    public bool Take(string name) => _pending.TryRemove(name, out var at) && DateTime.UtcNow - at <= Lifetime;
}
