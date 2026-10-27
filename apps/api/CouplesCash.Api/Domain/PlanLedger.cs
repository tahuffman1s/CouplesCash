namespace CouplesCash.Api.Domain;

/// <summary>A bills-account row of the pay week as the ledger sees it (signed amount; the bill it pays, when matched).</summary>
public sealed record LedgerTransaction(
    Guid Id,
    DateOnly PostedOn,
    long AmountCents,
    string Name,
    bool Pending,
    bool Added,
    Guid? ItemId,
    DateOnly? DueDate);

/// <summary>One line of a ledger day: a transaction, or the bill / transfer / income it paid (then named after that item).</summary>
public sealed record LedgerEntry(Guid TransactionId, string Kind, string Name, long AmountCents, bool Pending, bool Added, Guid? ItemId, DateOnly? DueDate);

public sealed record LedgerDay(DateOnly Date, IReadOnlyList<LedgerEntry> Entries);

/// <summary>
/// The pay week as a ledger: the start-of-week amount, each day from payday through today with what moved through the bills account,
/// available now, and what is still to come (<see cref="Upcoming"/>, the plan lines the bank has not paid). Start + every day = available.
/// </summary>
public sealed record Ledger(long StartCents, IReadOnlyList<LedgerDay> Days, long AvailableCents, IReadOnlyList<PlanOccurrence> Upcoming);

/// <summary>
/// Pure, unit-tested: PLAN-V7 "Friday plan as a ledger". The start of the week is worked back from available now: every bills-account
/// transaction dated in the week so far (pending, added by hand, transfers — anything that moved the balance) is taken off again, so the
/// days always add up to available. A row dated after today (a pending charge on its expected post day) is listed under today. A row matched
/// to an item shows as that item, never twice. Still to come = the week's bills, fun money and one-offs the bank has not paid (ticked by hand
/// or not — the money is still in available) and income not received yet, by due date.
/// </summary>
public static class PlanLedger
{
    public static Ledger Build(
        WeekWindow week,
        DateOnly today,
        long availableCents,
        IEnumerable<LedgerTransaction> transactions,
        IEnumerable<(PlanOccurrence Occurrence, PlanBucket Bucket)> lines,
        IReadOnlyDictionary<Guid, (string Name, Kind Kind, Frequency Frequency)> items)
    {
        var last = today < week.End ? today : week.End;
        var moved = transactions.Where(t => t.PostedOn >= week.Start).ToList();
        var start = availableCents - moved.Sum(t => t.AmountCents);

        var days = new List<LedgerDay>();
        for (var day = week.Start; day <= last; day = day.AddDays(1))
        {
            var date = day;
            var entries = moved
                .Where(t => (t.PostedOn < last ? t.PostedOn : last) == date)
                .OrderBy(t => t.PostedOn)
                .ThenBy(t => t.Pending)
                .ThenBy(t => t.Id)
                .Select(t => Entry(t, items))
                .ToList();
            days.Add(new LedgerDay(date, entries));
        }

        var upcoming = lines
            .Where(l => week.Contains(l.Occurrence.DueDate) && !l.Occurrence.PaidFromBank
                && l.Bucket is PlanBucket.Bill or PlanBucket.FunMoney or PlanBucket.OneOff or PlanBucket.Income)
            .Select(l => l.Occurrence)
            .OrderBy(o => o.DueDate)
            .ToList();

        return new Ledger(start, days, availableCents, upcoming);
    }

    /// <summary>
    /// Projected amount in savings: the savings accounts' balance, minus their bills still due this week, plus this week's projected savings
    /// while it has not moved yet (never negative; once the sweep is done it is already in the balance).
    /// </summary>
    public static long ProjectedSavings(long savingsBalanceCents, long savingsBillsDueCents, long projectedCents, bool sweepDone) =>
        savingsBalanceCents - savingsBillsDueCents + (sweepDone ? 0 : Math.Max(0, projectedCents));

    private static LedgerEntry Entry(LedgerTransaction t, IReadOnlyDictionary<Guid, (string Name, Kind Kind, Frequency Frequency)> items)
    {
        if (t.ItemId is { } itemId && items.TryGetValue(itemId, out var item))
        {
            var kind = item.Frequency == Frequency.Once && item.Kind != Kind.Income ? "oneoff" : item.Kind.ToDb();
            return new LedgerEntry(t.Id, kind, item.Name, t.AmountCents, t.Pending, t.Added, itemId, t.DueDate);
        }

        return new LedgerEntry(t.Id, "transaction", t.Name, t.AmountCents, t.Pending, t.Added, null, null);
    }
}
