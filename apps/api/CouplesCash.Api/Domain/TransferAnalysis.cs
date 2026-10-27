namespace CouplesCash.Api.Domain;

/// <summary>A row ignored as a transfer (or the peer of one). <paramref name="AmountCents"/> is signed; null account = not mapped.</summary>
public sealed record TransferLeg(Guid Id, Guid? AccountId, string? AccountName, DateOnly PostedOn, long AmountCents, Guid? PeerId, bool Auto);

/// <summary>A paired transfer: money left <see cref="From"/> and arrived in <see cref="To"/>. Id = the outflow row id.</summary>
public sealed record TransferPairResult(Guid Id, DateOnly Date, long AmountCents, TransferLeg From, TransferLeg To, bool Auto);

/// <summary>Flow between two accounts; a null id is "Other account" (one-sided transfers, unmapped rows).</summary>
public sealed record TransferFlow(Guid? FromAccountId, string FromName, Guid? ToAccountId, string ToName, long TotalCents, int Count);

public sealed record TransferNet(Guid? AccountId, string Name, long InCents, long OutCents)
{
    public long NetCents => InCents - OutCents;
}

public sealed record TransferAnalysisResult(
    long TotalCents,
    IReadOnlyList<TransferFlow> Flows,
    IReadOnlyList<TransferNet> NetByAccount,
    IReadOnlyList<TransferPairResult> Pairs,
    IReadOnlyList<TransferLeg> OneSided);

/// <summary>
/// PLAN-V5 §2 transfer analysis, pure: pairs by <c>transfer_peer_id</c> (dated and counted by the outflow leg, which must fall in
/// [<c>from</c>, <c>to</c>]), one-sided legs against "Other account", flows grouped by (from, to), net in/out per account.
/// Transfers never count as spending, budgets or income — this is the only place they are summed.
/// </summary>
public static class TransferAnalysis
{
    public const string OtherAccount = "Other account";

    public static TransferAnalysisResult Analyse(IReadOnlyCollection<TransferLeg> legs, DateOnly from, DateOnly to)
    {
        var byId = legs.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        var pairs = new List<TransferPairResult>();
        var oneSided = new List<TransferLeg>();
        var used = new HashSet<Guid>();

        foreach (var leg in legs.OrderByDescending(l => l.PostedOn).ThenByDescending(l => l.Id))
        {
            if (used.Contains(leg.Id))
            {
                continue;
            }

            if (leg.PeerId is { } peerId && byId.TryGetValue(peerId, out var peer) && peer.PeerId == leg.Id && !used.Contains(peer.Id))
            {
                var (outLeg, inLeg) = leg.AmountCents < 0 ? (leg, peer) : (peer, leg);
                used.Add(leg.Id);
                used.Add(peer.Id);
                if (outLeg.PostedOn >= from && outLeg.PostedOn <= to)
                {
                    pairs.Add(new TransferPairResult(outLeg.Id, outLeg.PostedOn, Math.Abs(outLeg.AmountCents), outLeg, inLeg, outLeg.Auto || inLeg.Auto));
                }

                continue;
            }

            used.Add(leg.Id);
            if (leg.PostedOn >= from && leg.PostedOn <= to)
            {
                oneSided.Add(leg);
            }
        }

        var flowLegs = pairs
            .Select(p => (FromId: p.From.AccountId, FromName: Name(p.From), ToId: p.To.AccountId, ToName: Name(p.To), p.AmountCents))
            .Concat(oneSided.Select(l => l.AmountCents < 0
                ? (FromId: l.AccountId, FromName: Name(l), ToId: (Guid?)null, ToName: OtherAccount, AmountCents: Math.Abs(l.AmountCents))
                : (FromId: (Guid?)null, FromName: OtherAccount, ToId: l.AccountId, ToName: Name(l), AmountCents: Math.Abs(l.AmountCents))))
            .ToList();

        var flows = flowLegs
            .GroupBy(f => (f.FromId, f.ToId))
            .Select(g => new TransferFlow(g.Key.FromId, g.First().FromName, g.Key.ToId, g.First().ToName, g.Sum(f => f.AmountCents), g.Count()))
            .OrderByDescending(f => f.TotalCents).ThenBy(f => f.FromName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.ToName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var net = new Dictionary<Guid, (string Name, long In, long Out)>();
        foreach (var leg in pairs.SelectMany(p => new[] { p.From, p.To }).Concat(oneSided).Where(l => l.AccountId is not null))
        {
            var id = leg.AccountId!.Value;
            var current = net.TryGetValue(id, out var v) ? v : (Name(leg), 0L, 0L);
            net[id] = leg.AmountCents >= 0
                ? (current.Item1, current.Item2 + leg.AmountCents, current.Item3)
                : (current.Item1, current.Item2, current.Item3 - leg.AmountCents);
        }

        var netByAccount = net
            .Select(kv => new TransferNet(kv.Key, kv.Value.Name, kv.Value.In, kv.Value.Out))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.AccountId)
            .ToList();

        return new TransferAnalysisResult(flows.Sum(f => f.TotalCents), flows, netByAccount, pairs, oneSided);
    }

    private static string Name(TransferLeg leg) => leg.AccountId is null ? OtherAccount : leg.AccountName ?? OtherAccount;
}

/// <summary>Bill detail "Payments" (v5): which occurrence dates to show.</summary>
public static class PaymentHistory
{
    public const int DefaultLimit = 12;
    public const int MaxLimit = 60;

    /// <summary>
    /// Newest first: the next occurrence after <paramref name="today"/> (when there is one), then the last <paramref name="limit"/>
    /// occurrences on or before today.
    /// </summary>
    public static IReadOnlyList<DateOnly> Dates(Recurrence rule, DateOnly today, int limit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var spanDays = rule.Frequency switch
        {
            Frequency.Weekly => 7,
            Frequency.Biweekly => 14,
            Frequency.Monthly => 31,
            Frequency.Quarterly => 92,
            Frequency.Yearly => 366,
            _ => 0,
        };

        // An ended item looks back from its end date, not from today.
        var until = rule.EndDate is { } end && end < today ? end : today;
        var from = spanDays == 0 ? rule.AnchorDate : Max(rule.AnchorDate, until.AddDays(-(spanDays * (limit + 1))));
        List<DateOnly> past = from > until ? [] : RecurrenceEngine.Expand(rule, from, until).TakeLast(limit).Reverse().ToList();
        var next = RecurrenceEngine.NextDue(rule, today.AddDays(1));

        var result = new List<DateOnly>(past.Count + 1);
        if (next is { } n && n > today)
        {
            result.Add(n);
        }

        result.AddRange(past);
        return result;
    }

    /// <summary>
    /// The slot listed after <see cref="Dates"/> for a payment from before the item started: its <see cref="RecurrenceEngine.EarlierDate"/>,
    /// once the list reaches back to the start, and not in the future. Null otherwise.
    /// </summary>
    public static DateOnly? EarlierSlot(Recurrence rule, IReadOnlyList<DateOnly> dates, DateOnly today) =>
        dates.Contains(rule.AnchorDate) && RecurrenceEngine.EarlierDate(rule) is { } earlier && earlier <= today ? earlier : null;

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
