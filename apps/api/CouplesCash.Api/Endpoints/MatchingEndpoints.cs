using System.Globalization;
using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Matching;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

/// <summary>v6: the fuzzy matcher's status and "re-score my To-review rows now".</summary>
public static class MatchingEndpoints
{
    /// <summary>How long the status endpoint waits for the matcher's <c>/health</c>.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static RouteGroupBuilder MapMatching(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("").WithTags("Matching").RequireAuthorization().RequireHousehold();

        group.MapGet("/matcher/status", Status)
            .WithSummary("Matcher engine (fuzzy | builtin), reachability, version, this household's last run and its auto-matched count");
        group.MapPost("/inbox/rematch", Rematch)
            .WithSummary("Re-score every To-review row now (202, queued, debounced 10 s) · 429 more than once a minute per household");

        return group;
    }

    private static async Task<Ok<MatcherStatusDto>> Status(
        ClaimsPrincipal principal,
        IMatcherClient matcher,
        MatcherStatusTracker tracker,
        TimeProvider time,
        CancellationToken ct)
    {
        var now = time.Now();
        var run = tracker.LastRun(principal.GetHouseholdId());
        if (!matcher.Enabled)
        {
            return TypedResults.Ok(new MatcherStatusDto(MatchDetail.Builtin, false, null, run?.At, run?.AutoMatched ?? 0, false, run?.Engine, false,
                "The matcher is not configured (Matcher__BaseUrl, Matcher__Key): matching uses the built-in engine and found bills are paused."));
        }

        var (reachable, version) = tracker.CachedHealth(now) ?? await ProbeAsync(matcher, tracker, now, ct);
        var failing = tracker.LastFailure;
        var engine = reachable && failing is null ? MatchDetail.Fuzzy : MatchDetail.Builtin;
        string? note = !reachable
            ? "The matcher is unreachable: matching falls back to the built-in engine and found bills are paused until it is back."
            : failing is { } f
                ? string.Create(CultureInfo.InvariantCulture, $"The last matcher call failed ({f}); the built-in engine is used until a call succeeds.")
                : null;
        return TypedResults.Ok(new MatcherStatusDto(engine, reachable, version, run?.At, run?.AutoMatched ?? 0, true, run?.Engine, reachable, note));
    }

    private static async Task<(bool Reachable, string? Version)> ProbeAsync(IMatcherClient matcher, MatcherStatusTracker tracker, DateTime now, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var health = await matcher.HealthAsync(timeout.Token);
            tracker.RecordProbe(true, health.Version, now);
            return (true, health.Version);
        }
        catch (MatcherException)
        {
            tracker.RecordProbe(false, null, now);
            return (false, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tracker.RecordProbe(false, null, now);
            return (false, null);
        }
    }

    private static Results<Accepted<RematchQueuedDto>, ProblemHttpResult> Rematch(ClaimsPrincipal principal, HttpContext http, MatchingQueue queue)
    {
        if (queue.TryRequestManual(principal.GetHouseholdId()) is { } retryAfter)
        {
            var seconds = BankSyncRules.RetryAfterSeconds(retryAfter);
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return TypedResults.Problem(
                title: "Too many requests",
                detail: $"A rematch was requested less than a minute ago. Retry in {seconds} second(s).",
                statusCode: StatusCodes.Status429TooManyRequests,
                extensions: new Dictionary<string, object?> { ["retryAfter"] = seconds });
        }

        return TypedResults.Accepted("/v1/matcher/status", new RematchQueuedDto(true));
    }
}
