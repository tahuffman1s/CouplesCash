using System.Collections.Concurrent;
using System.Threading.Channels;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Matching;

/// <summary>
/// Hand-off from requests to the background <c>MatchingWorker</c>: "rematch this household" (an item was created / edited, bank names or
/// the auto level changed, "Rematch now") and "look for found bills" (after a sync or CSV import). The worker debounces per household.
/// Also holds the per-household 1/min limit of <c>POST /v1/inbox/rematch</c>.
/// </summary>
public sealed class MatchingQueue(TimeProvider time)
{
    private readonly Channel<(Guid HouseholdId, MatchingJobKind Kind)> _channel = Channel.CreateBounded<(Guid, MatchingJobKind)>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly ConcurrentDictionary<Guid, DateTime> _manual = new();
    private readonly Lock _gate = new();

    public ChannelReader<(Guid HouseholdId, MatchingJobKind Kind)> Reader => _channel.Reader;

    public void Enqueue(Guid householdId, MatchingJobKind kind) => _channel.Writer.TryWrite((householdId, kind));

    /// <summary>Queues a manual rematch unless one was requested less than a minute ago; returns the time left in that case.</summary>
    public TimeSpan? TryRequestManual(Guid householdId)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            var retryAfter = RematchRules.RetryAfter(_manual.TryGetValue(householdId, out var last) ? last : null, now);
            if (retryAfter is not null)
            {
                return retryAfter;
            }

            _manual[householdId] = now;
        }

        Enqueue(householdId, MatchingJobKind.Rematch);
        return null;
    }
}
