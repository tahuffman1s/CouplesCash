namespace CouplesCash.Api.Domain;

/// <summary>Pure timing rules for bank sync: scheduled interval, manual cooldown, fetch window, liability sign.</summary>
public static class BankSyncRules
{
    public const int FirstSyncDays = 90;
    public const int RegularSyncDays = 30;

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

    /// <summary>Fetch window: 90 days back on the first sync, 30 after; the end is tomorrow so today's transactions are included (SimpleFIN end-date is exclusive).</summary>
    public static (DateOnly Start, DateOnly EndExclusive) FetchWindow(DateTime? lastSyncedAtUtc, DateOnly today) =>
        (today.AddDays(-(lastSyncedAtUtc is null ? FirstSyncDays : RegularSyncDays)), today.AddDays(1));

    /// <summary>The Bridge reports credit/loan balances negative-when-owed; CouplesCash stores liabilities as positive amount owed.</summary>
    public static long ToAccountBalance(AccountType type, long bridgeBalanceCents) =>
        type.IsLiability() ? -bridgeBalanceCents : bridgeBalanceCents;
}
