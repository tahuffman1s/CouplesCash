using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Services;

/// <summary>
/// Turns goal rows into <see cref="GoalDto"/>s: history (account snapshots for linked goals, running sum
/// of contributions for manual ones; ≤ 90 points, oldest first) plus the <see cref="GoalProjection"/>.
/// </summary>
public sealed class GoalService(GoalRepository goals, AccountRepository accounts, TimeProvider time)
{
    public const int HistoryPoints = 90;

    public async Task<GoalDto> ToDtoAsync(Guid householdId, GoalRow goal, CancellationToken ct) =>
        (await ToDtosAsync(householdId, [goal], ct))[0];

    public async Task<IReadOnlyList<GoalDto>> ToDtosAsync(Guid householdId, IReadOnlyList<GoalRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var linkedAccounts = rows.Where(g => g.AccountId is not null).Select(g => g.AccountId!.Value).Distinct().ToList();
        var manualGoals = rows.Where(g => g.AccountId is null).Select(g => g.Id).ToList();

        var snapshots = (await accounts.RecentSnapshotsAsync(householdId, linkedAccounts, HistoryPoints, ct))
            .GroupBy(s => s.AccountId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.RecordedAt).Select(s => new GoalPoint(s.RecordedAt, s.BalanceCents)).ToList());
        var contributions = (await goals.ContributionsAsync(householdId, manualGoals, ct))
            .GroupBy(c => c.GoalId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CreatedAt).ToList());

        var now = time.GetUtcNow().UtcDateTime;
        return rows.Select(g =>
        {
            var history = g.AccountId is { } accountId
                ? snapshots.GetValueOrDefault(accountId) ?? []
                : RunningSum(g, contributions.GetValueOrDefault(g.Id) ?? []);
            return ToDto(g, history, now);
        }).ToList();
    }

    /// <summary>Manual goals start at 0 when created; each contribution adds a point.</summary>
    public static List<GoalPoint> RunningSum(GoalRow goal, IReadOnlyList<ContributionRow> contributions)
    {
        var points = new List<GoalPoint>(contributions.Count + 1) { new(goal.CreatedAt, 0) };
        long running = 0;
        foreach (var c in contributions)
        {
            running += c.AmountCents;
            points.Add(new GoalPoint(c.CreatedAt, running));
        }

        return points.Count <= HistoryPoints ? points : points[^HistoryPoints..];
    }

    public static GoalDto ToDto(GoalRow g, IReadOnlyList<GoalPoint> history, DateTime nowUtc)
    {
        var current = g.CurrentCents;
        var forecast = GoalProjection.Project(history, g.TargetCents, current, g.TargetDate, nowUtc);
        var percent = g.TargetCents <= 0 ? 0 : (int)Math.Clamp(Math.Round(current * 100m / g.TargetCents, 0, MidpointRounding.AwayFromZero), 0, 100);
        return new GoalDto(
            g.Id,
            g.Name,
            g.TargetCents,
            g.TargetDate,
            g.AccountId,
            g.AccountName,
            g.Color,
            g.Icon,
            current,
            percent,
            forecast.MonthlyPaceCents,
            forecast.ProjectedDate,
            forecast.MonthlyNeededCents,
            history.Select(p => new GoalHistoryPoint(p.At, p.Value)).ToList(),
            g.CreatedAt);
    }
}
