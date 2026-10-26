using CouplesCash.Api.Domain;
using CouplesCash.Api.Matching;
using CouplesCash.Api.Services;

namespace CouplesCash.Api.Scheduler;

/// <summary>
/// Drains <see cref="MatchingQueue"/>: each (household, job) waits <c>Matcher__DebounceSeconds</c> (10 s) after its latest request
/// (at most 60 s after the first), then runs — a rematch of every To-review row, or found-bills detection. Runs even when the periodic
/// scheduler is disabled, since the jobs are triggered by people. The logger category contains "Scheduler".
/// </summary>
public sealed class MatchingWorker(
    MatchingQueue queue,
    MatchingService matching,
    BillSuggestionService bills,
    MatcherOptions options,
    TimeProvider time,
    ILogger<MatchingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var debouncer = new RematchDebouncer(options.Debounce, RematchDebouncer.DefaultMaxWait);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var wait = debouncer.NextDue is { } due ? due - now : Timeout.InfiniteTimeSpan;
            if (wait == Timeout.InfiniteTimeSpan || wait > TimeSpan.Zero)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                if (wait != Timeout.InfiniteTimeSpan)
                {
                    timeout.CancelAfter(wait);
                }

                try
                {
                    await queue.Reader.WaitToReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The debounce timer fired.
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            now = time.GetUtcNow().UtcDateTime;
            while (queue.Reader.TryRead(out var job))
            {
                debouncer.Request(job.HouseholdId, job.Kind, now);
            }

            foreach (var (householdId, kind) in debouncer.TakeDue(now))
            {
                await RunAsync(householdId, kind, stoppingToken);
            }
        }
    }

    private async Task RunAsync(Guid householdId, MatchingJobKind kind, CancellationToken ct)
    {
        try
        {
            if (kind == MatchingJobKind.Rematch)
            {
                var result = await matching.RematchAsync(householdId, ct);
                logger.LogInformation("Scheduler immediate rematch of household {HouseholdId}: {Scored} scored ({Engine}), {Auto} auto-applied",
                    householdId, result.Scored, result.Engine, result.AutoMatched + result.AutoCategorized);
            }
            else
            {
                await bills.DetectAsync(householdId, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduler matching job {Kind} for household {HouseholdId} failed", kind, householdId);
        }
    }
}
