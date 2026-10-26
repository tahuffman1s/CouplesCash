namespace CouplesCash.Api.Domain;

/// <summary>What the background matching worker does for a household.</summary>
public enum MatchingJobKind
{
    /// <summary>Re-score every To-review row (an item was created / edited, bank names changed, the auto level changed, "Rematch now").</summary>
    Rematch,

    /// <summary>Look for regular payments in the household's imported transactions (after a sync / CSV import).</summary>
    FoundBills,
}

/// <summary>
/// Trailing debounce per (household, job): every request pushes the run <see cref="Delay"/> into the future, but a burst never waits
/// longer than <see cref="MaxWait"/> after its first request. Not thread-safe: owned by the single worker loop. Pure (time is passed in).
/// </summary>
public sealed class RematchDebouncer(TimeSpan delay, TimeSpan maxWait)
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(60);

    private readonly Dictionary<(Guid HouseholdId, MatchingJobKind Kind), (DateTime First, DateTime Due)> _pending = [];

    public RematchDebouncer()
        : this(DefaultDelay, DefaultMaxWait)
    {
    }

    public TimeSpan Delay { get; } = delay;

    public TimeSpan MaxWait { get; } = maxWait < delay ? delay : maxWait;

    public int Count => _pending.Count;

    public void Request(Guid householdId, MatchingJobKind kind, DateTime now)
    {
        var key = (householdId, kind);
        var first = _pending.TryGetValue(key, out var existing) ? existing.First : now;
        var due = now + Delay;
        var cap = first + MaxWait;
        _pending[key] = (first, due < cap ? due : cap);
    }

    /// <summary>The earliest due time, or null when nothing is waiting.</summary>
    public DateTime? NextDue => _pending.Count == 0 ? null : _pending.Values.Min(v => v.Due);

    /// <summary>Removes and returns every job due at <paramref name="now"/>, oldest due first.</summary>
    public IReadOnlyList<(Guid HouseholdId, MatchingJobKind Kind)> TakeDue(DateTime now)
    {
        var due = _pending.Where(p => p.Value.Due <= now).OrderBy(p => p.Value.Due).Select(p => p.Key).ToList();
        foreach (var key in due)
        {
            _pending.Remove(key);
        }

        return due;
    }
}

/// <summary>Rate limits and cadences of the v6 matching jobs. Pure.</summary>
public static class RematchRules
{
    /// <summary><c>POST /v1/inbox/rematch</c>: once per minute per household.</summary>
    public static readonly TimeSpan ManualInterval = TimeSpan.FromMinutes(1);

    /// <summary>Found bills: at least daily (and after each sync).</summary>
    public static readonly TimeSpan FoundBillsInterval = TimeSpan.FromHours(24);

    /// <summary>Time left before another manual rematch is allowed, or null when it is allowed now.</summary>
    public static TimeSpan? RetryAfter(DateTime? lastRequestUtc, DateTime nowUtc, TimeSpan? interval = null)
    {
        var window = interval ?? ManualInterval;
        if (lastRequestUtc is null || window <= TimeSpan.Zero)
        {
            return null;
        }

        var remaining = lastRequestUtc.Value + window - nowUtc;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    /// <summary>A periodic job is due when it never ran (in this process) or ran at least <paramref name="interval"/> ago. Interval ≤ 0 = every tick.</summary>
    public static bool IsDue(DateTime? lastRunUtc, DateTime nowUtc, TimeSpan interval) =>
        interval <= TimeSpan.Zero || lastRunUtc is null || nowUtc - lastRunUtc.Value >= interval;
}
