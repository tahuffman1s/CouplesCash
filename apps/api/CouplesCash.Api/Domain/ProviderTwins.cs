namespace CouplesCash.Api.Domain;

/// <summary>A posted bank row as the cross-provider dedupe sees it (signed amount).</summary>
public sealed record TwinRow(Guid Id, DateOnly PostedOn, long AmountCents);

/// <summary>
/// Pure, unit-tested: the same bank account linked through two providers (SimpleFIN and Plaid, e.g. while switching) reports
/// every transaction twice under different ids. A <b>posted</b> row from the newcomer is the twin of a posted row the other
/// provider already brought when the amount is exactly the same and the dates are at most <see cref="MaxDays"/> apart —
/// closest date first, then the oldest row, one-to-one. Pending rows are never paired (their amounts and dates still move).
/// </summary>
public static class ProviderTwins
{
    public const int MaxDays = 3;

    public static IReadOnlyList<(Guid NewId, Guid ExistingId)> Match(IReadOnlyList<TwinRow> newcomers, IReadOnlyList<TwinRow> existing)
    {
        var pairs = (
            from n in newcomers
            from e in existing
            where n.AmountCents == e.AmountCents && n.AmountCents != 0
            let days = Math.Abs(n.PostedOn.DayNumber - e.PostedOn.DayNumber)
            where days <= MaxDays
            select (New: n, Existing: e, Days: days))
            .OrderBy(x => x.Days)
            .ThenBy(x => x.Existing.PostedOn)
            .ThenBy(x => x.Existing.Id)
            .ThenBy(x => x.New.Id)
            .ToList();

        var usedNew = new HashSet<Guid>();
        var usedExisting = new HashSet<Guid>();
        var result = new List<(Guid, Guid)>();
        foreach (var (n, e, _) in pairs)
        {
            if (usedNew.Contains(n.Id) || usedExisting.Contains(e.Id))
            {
                continue;
            }

            usedNew.Add(n.Id);
            usedExisting.Add(e.Id);
            result.Add((n.Id, e.Id));
        }

        return result;
    }
}
