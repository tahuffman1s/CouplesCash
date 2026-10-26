namespace CouplesCash.Api.Domain;

/// <summary>Pure timing rules for bank sync: scheduled interval, manual cooldown, fetch window, liability sign.</summary>
public static class BankSyncRules
{
    public const int FirstSyncDays = 90;
    public const int RegularSyncDays = 30;

    /// <summary>The window runs this far ahead: some banks date a pending charge on the day they expect it to post.</summary>
    public const int LookAheadDays = 14;

    /// <summary>Days per Bridge request: it warns (in <c>errors</c>) above 45 and cuts anything over 90.</summary>
    public const int MaxRequestDays = 44;

    /// <summary>
    /// A link is due when it never synced or its last attempt (successful or failed — a broken link is retried at the same
    /// cadence, not every tick) is older than the interval. Interval ≤ 0 = every tick (tests).
    /// </summary>
    public static bool IsDue(DateTime? lastSyncedAtUtc, DateTime? lastAttemptAtUtc, DateTime nowUtc, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return true;
        }

        var reference = Max(lastSyncedAtUtc, lastAttemptAtUtc);
        return reference is null || nowUtc - reference.Value >= interval;
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Time left on the manual "Sync now" cooldown, or null when a manual sync is allowed.</summary>
    public static TimeSpan? CooldownRemaining(DateTime? lastManualSyncAtUtc, DateTime nowUtc, TimeSpan cooldown)
    {
        if (lastManualSyncAtUtc is null || cooldown <= TimeSpan.Zero)
        {
            return null;
        }

        var remaining = lastManualSyncAtUtc.Value + cooldown - nowUtc;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    /// <summary>Whole seconds for a <c>Retry-After</c> header (at least 1).</summary>
    public static int RetryAfterSeconds(TimeSpan remaining) => Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));

    /// <summary>
    /// Fetch window: 90 days back on the first sync, 30 after, through <see cref="LookAheadDays"/> ahead (SimpleFIN end-date is
    /// exclusive) — pending charges dated on their expected post day and this evening's charges (end-date is midnight UTC) are in it.
    /// </summary>
    public static (DateOnly Start, DateOnly EndExclusive) FetchWindow(DateTime? lastSyncedAtUtc, DateOnly today) =>
        (today.AddDays(-(lastSyncedAtUtc is null ? FirstSyncDays : RegularSyncDays)), today.AddDays(LookAheadDays));

    /// <summary>
    /// The Bridge requests that cover a window, newest first, each at most <see cref="MaxRequestDays"/> long. Only the newest asks for
    /// pending transactions (they are recent); the older ones are history for the first sync.
    /// </summary>
    public static IReadOnlyList<(DateOnly Start, DateOnly EndExclusive, bool Pending)> FetchRequests(DateOnly start, DateOnly endExclusive)
    {
        var requests = new List<(DateOnly, DateOnly, bool)>();
        for (var end = endExclusive; end > start; end = end.AddDays(-MaxRequestDays))
        {
            var from = end.AddDays(-MaxRequestDays);
            requests.Add((from < start ? start : from, end, requests.Count == 0));
        }

        return requests;
    }

    /// <summary>The Bridge reports credit/loan balances negative-when-owed; CouplesCash stores liabilities as positive amount owed.</summary>
    public static long ToAccountBalance(AccountType type, long bridgeBalanceCents) =>
        type.IsLiability() ? -bridgeBalanceCents : bridgeBalanceCents;
}
