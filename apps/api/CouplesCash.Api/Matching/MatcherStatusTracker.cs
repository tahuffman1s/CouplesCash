using System.Collections.Concurrent;

namespace CouplesCash.Api.Matching;

/// <summary>The last matching run of a household (in memory; empty after a restart).</summary>
public sealed record MatcherRun(DateTime At, string Engine, int AutoMatched, int Scored);

/// <summary>
/// Process-wide matcher bookkeeping for <c>GET /v1/matcher/status</c> and the fallback warning: per-household last run, the last health
/// probe (cached briefly), the outcome of the last real call, and the once-per-minute warning throttle.
/// </summary>
public sealed class MatcherStatusTracker
{
    public static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan HealthCacheTtl = TimeSpan.FromSeconds(15);

    /// <summary>"Unreachable" is re-checked sooner, so the status turns green right after the matcher comes back.</summary>
    public static readonly TimeSpan UnreachableCacheTtl = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, MatcherRun> _runs = new();
    private readonly Lock _gate = new();
    private DateTime? _lastWarningAt;
    private (DateTime At, bool Reachable, string? Version)? _probe;

    /// <summary>Failure of the most recent real call (match / detect), or null when it succeeded (or none happened yet).</summary>
    public MatcherFailure? LastFailure { get; private set; }

    public DateTime? LastFailureAt { get; private set; }

    public string? LastVersion { get; private set; }

    public DateTime? LastFoundBillsAt { get; private set; }

    public void RecordRun(Guid householdId, MatcherRun run) => _runs[householdId] = run;

    public MatcherRun? LastRun(Guid householdId) => _runs.TryGetValue(householdId, out var run) ? run : null;

    public void RecordFoundBills(DateTime at) => LastFoundBillsAt = at;

    /// <summary>Remembers a failed call; returns true when the caller should log the warning (at most once per <see cref="WarningInterval"/>).</summary>
    public bool RecordFailure(MatcherFailure failure, DateTime nowUtc)
    {
        lock (_gate)
        {
            LastFailure = failure;
            LastFailureAt = nowUtc;
            if (failure is MatcherFailure.Unreachable or MatcherFailure.Timeout)
            {
                _probe = (nowUtc, false, _probe?.Version);
            }

            if (_lastWarningAt is { } last && nowUtc - last < WarningInterval)
            {
                return false;
            }

            _lastWarningAt = nowUtc;
            return true;
        }
    }

    /// <summary>A real call succeeded (it also proves the service is reachable).</summary>
    public void RecordSuccess(string? version, DateTime nowUtc)
    {
        lock (_gate)
        {
            LastFailure = null;
            if (!string.IsNullOrWhiteSpace(version))
            {
                LastVersion = version;
            }

            _probe = (nowUtc, true, LastVersion ?? _probe?.Version);
        }
    }

    public void RecordProbe(bool reachable, string? version, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(version))
            {
                LastVersion = version;
            }

            _probe = (nowUtc, reachable, reachable ? LastVersion : _probe?.Version);

            // A successful probe proves a network failure is over; a 401 / 413 / 422 / 5xx is only cleared by a successful call.
            if (reachable && LastFailure is MatcherFailure.Unreachable or MatcherFailure.Timeout)
            {
                LastFailure = null;
            }
        }
    }

    /// <summary>
    /// A recent reachability observation (probe or real call), or null when it is older than <see cref="HealthCacheTtl"/>
    /// (<see cref="UnreachableCacheTtl"/> for "unreachable").
    /// </summary>
    public (bool Reachable, string? Version)? CachedHealth(DateTime nowUtc)
    {
        lock (_gate)
        {
            return _probe is { } h && nowUtc - h.At < (h.Reachable ? HealthCacheTtl : UnreachableCacheTtl) ? (h.Reachable, h.Version) : null;
        }
    }
}
