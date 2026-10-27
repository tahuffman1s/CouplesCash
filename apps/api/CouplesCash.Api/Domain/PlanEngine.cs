namespace CouplesCash.Api.Domain;

/// <summary>Where an occurrence goes in the Friday plan.</summary>
public enum PlanBucket
{
    /// <summary>Not part of the plan (another account's bill, a transfer between two non-bills accounts, …).</summary>
    None,

    /// <summary>A bill / subscription (or a transfer out to a non-fun account) paid from the bills account or unassigned.</summary>
    Bill,

    /// <summary>A transfer from the bills account to a <c>fun</c> account.</summary>
    FunMoney,

    /// <summary>A planned one-off (<c>frequency: once</c>) paid from the bills account or unassigned.</summary>
    OneOff,

    /// <summary>Income landing in the bills account (or unassigned), or a transfer into the bills account.</summary>
    Income,

    /// <summary>A bill / subscription paid from a <c>savings</c> account (student loans): shown, never in the math.</summary>
    SavingsBill,
}

/// <summary>
/// An occurrence as the plan sees it. <paramref name="PaidCents"/> is the paid amount (null = unpaid); <paramref name="PaidFromBank"/> = the
/// payment came from a matched bank transaction (so it is already reflected in the bank balance).
/// </summary>
public sealed record PlanOccurrence(
    Guid ItemId,
    Kind Kind,
    Frequency Frequency,
    long AmountCents,
    DateOnly DueDate,
    long? PaidCents,
    bool PaidFromBank,
    Guid? AccountId,
    Guid? FromAccountId,
    Guid? ToAccountId)
{
    public bool IsPaid => PaidCents is not null;
}

/// <summary>
/// The plan's sums over every occurrence of the pay week, paid or not, and the number she writes down. <see cref="PaidThisWeekCents"/> is
/// what the bank already paid for those lines, added back on top of the balance so nothing counts twice.
/// </summary>
public readonly record struct PlanTotals(long BillsOutCents, long FunOutCents, long OneOffOutCents, long IncomeInCents, long LeftAtEndCents, long PaidThisWeekCents = 0);

/// <summary>A bank row that may be the bills-account side of a planned transfer or of the savings sweep.</summary>
/// <param name="AmountCents">Signed (negative = money out of <paramref name="AccountId"/>).</param>
/// <param name="PeerId">Already paired transfer peer (v5), if any.</param>
/// <param name="LooksLikeTransfer">Transfer keyword, learned transfer payee or a detected pair suggestion (computed by the caller).</param>
/// <param name="Text">Description + payee, used to see whether the row names the destination account.</param>
public sealed record PlanBankRow(Guid Id, Guid? AccountId, DateOnly PostedOn, long AmountCents, Guid? PeerId, Guid? PeerAccountId, bool LooksLikeTransfer, string Text)
{
    public long Magnitude => Math.Abs(AmountCents);
}

/// <summary>An unpaid occurrence of a recurring transfer, with what we know about its destination account.</summary>
public sealed record PlannedTransfer(Guid ItemId, DateOnly DueDate, long AmountCents, Guid FromAccountId, Guid ToAccountId, bool ToLinked, string ToName, string ToInstitution);

/// <summary>A planned transfer paid by <paramref name="OutflowId"/> (bills side); <paramref name="InflowId"/> = the other leg when the bank feed shows it.</summary>
public sealed record PlannedTransferMatch(Guid ItemId, DateOnly DueDate, Guid OutflowId, Guid? InflowId, long AmountCents);

/// <summary>The bank rows that moved the sweep: the bills-side outflow and, when seen, the savings inflow.</summary>
public sealed record SweepMatch(Guid OutflowId, Guid? InflowId, long AmountCents);

/// <summary>
/// The Friday plan (PLAN-V7 "The Friday plan"), pure and unit-tested. The service loads accounts, occurrences, pending rows and the sweep;
/// everything that decides a number is here.
/// <list type="bullet">
/// <item><b>Pay week</b> = <see cref="WeekEngine.WeekBounds"/> anchored to the payday weekday: <c>[payday, payday + 6]</c>; the next payday
/// is <c>start + 7</c>.</item>
/// <item><b>Available now</b> = the bills account's bank <c>availableCents</c>, else <c>balanceCents − pendingOutCents</c>
/// (<see cref="WeekEngine.BillsAvailable"/>).</item>
/// <item><b>Left at the end</b> (<see cref="Totals"/>) = <c>available − unpaid bills − unpaid fun transfers − unpaid one-offs + unpaid income</c>,
/// over occurrences due in <c>[start, end]</c>. Income counts only when it lands strictly before the next payday (due ≤ end): the paycheck
/// due on the next payday belongs to the new week, and this week's paycheck, once it posts, is paid (and already in available), so it is
/// never counted twice.</item>
/// </list>
/// </summary>
public static class PlanEngine
{
    /// <summary>The checklist stays up from payday through payday + 3 while something is still open.</summary>
    public const int ChecklistDays = 3;

    /// <summary>Planned transfers: the bank amount within $1.00 …</summary>
    public const long TransferToleranceCents = 100;

    /// <summary>… and within ±2 days of the due date.</summary>
    public const int TransferWindowDays = 2;

    /// <summary>The sweep is looked for from the day before the sweep day (the last day of the pay week) …</summary>
    public const int SweepDaysBefore = 1;

    /// <summary>… to three days after it (a Thursday transfer can post over the weekend).</summary>
    public const int SweepDaysAfter = 3;

    /// <summary>Sweep amount tolerance floor: $5.00 (or 5 % of the planned amount, whichever is larger).</summary>
    public const long SweepMinToleranceCents = 500;

    public const double SweepTolerancePercent = 0.05;

    /// <summary>A savings-side inflow pairs with the sweep outflow within this many days.</summary>
    public const int SweepPairDays = 3;

    private static readonly HashSet<string> GenericAccountWords = new(StringComparer.Ordinal)
    {
        "checking", "check", "chk", "savings", "saving", "sav", "account", "acct", "bank", "the", "of", "and", "high", "yield", "fun", "money",
        "joint", "personal", "card", "credit", "union", "online", "mobile", "spending", "everyday", "interest", "plus", "premier", "basic",
    };

    // ---------------------------------------------------------------------------------------------
    // Classification and totals
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Which plan line an occurrence is. With a bills account (<paramref name="billsAccountId"/>): bills / subscriptions / income count when
    /// assigned to it or unassigned; a savings-account bill is a savings bill; a transfer out of the bills account is fun money when its
    /// destination is a <c>fun</c> account, else a bill line; a transfer into the bills account is income. Before setup (no bills account)
    /// every non-savings, non-fun account counts as the bills side.
    /// </summary>
    public static PlanBucket Classify(PlanOccurrence o, Guid? billsAccountId, IReadOnlyDictionary<Guid, AccountRole> roles)
    {
        bool BillsSide(Guid? accountId) => accountId is null
            ? true
            : billsAccountId is { } bills
                ? accountId == bills
                : RoleOf(accountId.Value) is not (AccountRole.Savings or AccountRole.Fun);

        AccountRole RoleOf(Guid id) => roles.TryGetValue(id, out var r) ? r : AccountRole.Other;

        switch (o.Kind)
        {
            case Kind.Transfer:
                if (o.FromAccountId is { } from && BillsSide(from))
                {
                    return o.ToAccountId is { } to && RoleOf(to) == AccountRole.Fun ? PlanBucket.FunMoney
                        : o.ToAccountId is { } to2 && BillsSide(to2) ? PlanBucket.None
                        : PlanBucket.Bill;
                }

                return o.ToAccountId is { } into && BillsSide(into) && o.FromAccountId is not null ? PlanBucket.Income : PlanBucket.None;

            case Kind.Income:
                return BillsSide(o.AccountId) ? PlanBucket.Income : PlanBucket.None;

            default:
                if (o.AccountId is { } account && RoleOf(account) == AccountRole.Savings)
                {
                    return PlanBucket.SavingsBill;
                }

                if (!BillsSide(o.AccountId))
                {
                    return PlanBucket.None;
                }

                return o.Frequency == Frequency.Once ? PlanBucket.OneOff : PlanBucket.Bill;
        }
    }

    /// <summary>The contract's <c>PlanLine.kind</c>: oneoff for planned one-offs, transfer for transfers, else the item kind.</summary>
    public static string LineKind(PlanBucket bucket, Kind kind) =>
        bucket == PlanBucket.OneOff ? "oneoff" : kind.ToDb();

    /// <summary>
    /// Sums of every bill, fun-money and one-off occurrence due in <paramref name="week"/> — paid or not; ticks are a to-do list — plus income
    /// not received yet, and
    /// <c>leftAtEnd = available + paidThisWeek − bills − fun − one-offs + income</c> (savings bills and unrelated items never count). A line paid
    /// from a bank transaction counts at the bank's amount and that money is already gone from <c>available</c>, so it is added back as
    /// <c>paidThisWeek</c>; one ticked by hand or by autopay is still in <c>available</c>. Either way ticking never moves the number.
    /// </summary>
    public static PlanTotals Totals(WeekWindow week, long availableCents, IEnumerable<(PlanOccurrence Occurrence, PlanBucket Bucket)> lines)
    {
        long bills = 0, fun = 0, oneOff = 0, income = 0, moved = 0;
        foreach (var (o, bucket) in lines.Where(l => week.Contains(l.Occurrence.DueDate)))
        {
            // Income still counts only until it arrives: this payday's paycheck is already the balance she starts from.
            if (bucket == PlanBucket.Income && o.PaidFromBank)
            {
                continue;
            }

            var amount = o.PaidFromBank ? o.PaidCents ?? o.AmountCents : o.AmountCents;
            switch (bucket)
            {
                case PlanBucket.Bill:
                    bills += amount;
                    break;
                case PlanBucket.FunMoney:
                    fun += amount;
                    break;
                case PlanBucket.OneOff:
                    oneOff += amount;
                    break;
                case PlanBucket.Income:
                    income += amount;
                    break;
                default:
                    continue;
            }

            if (o.PaidFromBank)
            {
                moved += amount;
            }
        }

        return new PlanTotals(bills, fun, oneOff, income, availableCents + moved - bills - fun - oneOff + income, moved);
    }

    /// <summary>Classify + <see cref="Totals"/> in one go.</summary>
    public static PlanTotals Totals(WeekWindow week, long availableCents, IEnumerable<PlanOccurrence> occurrences, Guid? billsAccountId, IReadOnlyDictionary<Guid, AccountRole> roles) =>
        Totals(week, availableCents, occurrences.Select(o => (o, Classify(o, billsAccountId, roles))));

    // ---------------------------------------------------------------------------------------------
    // Payday checklist
    // ---------------------------------------------------------------------------------------------

    /// <summary>Payday itself (household-local date).</summary>
    public static bool IsPayday(DateOnly today, int paydayWeekday) => (int)today.DayOfWeek == paydayWeekday;

    /// <summary>Shown on payday, and through payday + 3 while something is still open. Never for another week.</summary>
    public static bool ChecklistVisible(WeekWindow week, DateOnly today, bool allDone) =>
        today == week.Start || (today > week.Start && today <= week.Start.AddDays(ChecklistDays) && !allDone);

    /// <summary>The paycheck item: income lines due on payday.</summary>
    public static bool IsPaycheck(PlanOccurrence o, PlanBucket bucket, WeekWindow week) =>
        bucket == PlanBucket.Income && o.Kind == Kind.Income && o.DueDate == week.Start;

    /// <summary>Fun transfers on the checklist: those due payday … payday + 3 (the weekly ones on payday, a monthly one on its date when it falls in the window).</summary>
    public static bool IsChecklistFun(PlanOccurrence o, PlanBucket bucket, WeekWindow week) =>
        bucket == PlanBucket.FunMoney && o.DueDate >= week.Start && o.DueDate <= week.Start.AddDays(ChecklistDays);

    // ---------------------------------------------------------------------------------------------
    // Savings sweep
    // ---------------------------------------------------------------------------------------------

    /// <summary>What to move: the week's left-at-end, or nothing when it is negative.</summary>
    public static long SweepPlanned(long leftCents) => Math.Max(0, leftCents);

    /// <summary>The sweep day: the last day of the pay week ("Left on Thursday" is what moves to savings that day).</summary>
    public static DateOnly SweepDay(WeekWindow week) => week.End;

    /// <summary>
    /// The pay week whose sweep is frozen on <paramref name="today"/>: this week, on its last day (the sweep day); null on every other day.
    /// No catch-up: when the API was down all that day nothing is frozen and the money simply carries into the next week's plan.
    /// </summary>
    public static WeekWindow? SweepWeekToFreeze(DateOnly today, int paydayWeekday)
    {
        var week = WeekEngine.WeekBounds(today, paydayWeekday);
        return today == SweepDay(week) ? week : null;
    }

    /// <summary>max(5 % of the planned amount, $5.00).</summary>
    public static long SweepTolerance(long plannedCents) =>
        Math.Max(SweepMinToleranceCents, (long)Math.Round(plannedCents * SweepTolerancePercent, MidpointRounding.AwayFromZero));

    public static bool InSweepWindow(DateOnly postedOn, DateOnly sweepDay) =>
        postedOn >= sweepDay.AddDays(-SweepDaysBefore) && postedOn <= sweepDay.AddDays(SweepDaysAfter);

    /// <summary>
    /// The bills → savings move made on <paramref name="sweepDay"/>: a bills-account outflow posted sweep day − 1 … + 3
    /// within <see cref="SweepTolerance"/> of <paramref name="plannedCents"/>, backed by one of (strongest first) a transfer peer on a savings
    /// account, a savings-account inflow of the same amount (±1 %, at least $1, ±3 days), or — when the savings account has no bank feed, or
    /// the row names a savings account — a row that reads like a transfer. Closest amount, then closest to the sweep day, wins. Null when nothing fits
    /// or the plan is zero.
    /// </summary>
    public static SweepMatch? FindSweep(
        long plannedCents,
        DateOnly sweepDay,
        Guid billsAccountId,
        IReadOnlyCollection<Guid> savingsAccountIds,
        bool savingsLinked,
        IReadOnlyList<(string Name, string Institution)> savingsNames,
        IEnumerable<PlanBankRow> outflows,
        IEnumerable<PlanBankRow> savingsInflows)
    {
        if (plannedCents <= 0)
        {
            return null;
        }

        var tolerance = SweepTolerance(plannedCents);
        var inflows = savingsInflows.Where(r => r.AmountCents > 0 && r.AccountId is { } a && savingsAccountIds.Contains(a)).ToList();
        var best = outflows
            .Where(r => r.AccountId == billsAccountId && r.AmountCents < 0 && InSweepWindow(r.PostedOn, sweepDay))
            .Where(r => Math.Abs(r.Magnitude - plannedCents) <= tolerance)
            .Select(r => Evidence(r))
            .Where(x => x.Strength > 0)
            .OrderByDescending(x => x.Strength)
            .ThenBy(x => Math.Abs(x.Row.Magnitude - plannedCents))
            .ThenBy(x => Math.Abs(x.Row.PostedOn.DayNumber - sweepDay.DayNumber))
            .ThenBy(x => x.Row.Id)
            .FirstOrDefault();

        (PlanBankRow Row, Guid? Inflow, int Strength) Evidence(PlanBankRow r)
        {
            if (r.PeerId is { } peer && r.PeerAccountId is { } peerAccount)
            {
                return savingsAccountIds.Contains(peerAccount) ? (r, peer, 3) : (r, null, 0);
            }

            var inflow = inflows
                .Where(i => Math.Abs(i.PostedOn.DayNumber - r.PostedOn.DayNumber) <= SweepPairDays && Math.Abs(i.Magnitude - r.Magnitude) <= PairTolerance(r.Magnitude))
                .OrderBy(i => Math.Abs(i.PostedOn.DayNumber - r.PostedOn.DayNumber)).ThenBy(i => Math.Abs(i.Magnitude - r.Magnitude)).ThenBy(i => i.Id)
                .FirstOrDefault();
            if (inflow is not null)
            {
                return (r, inflow.Id, 2);
            }

            var named = savingsNames.Any(n => NamesAccount(r.Text, n.Name, n.Institution));
            return (!savingsLinked && r.LooksLikeTransfer) || named ? (r, null, 1) : (r, null, 0);
        }

        return best.Row is null ? null : new SweepMatch(best.Row.Id, best.Inflow, best.Row.Magnitude);
    }

    // ---------------------------------------------------------------------------------------------
    // Planned transfers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Planned amount within $1.00 and ±2 days of the due date.</summary>
    public static bool TransferMatches(long plannedCents, DateOnly dueDate, long magnitude, DateOnly postedOn) =>
        Math.Abs(magnitude - plannedCents) <= TransferToleranceCents && Math.Abs(postedOn.DayNumber - dueDate.DayNumber) <= TransferWindowDays;

    /// <summary>
    /// Matches unpaid planned transfers to bank rows, one-to-one. A candidate outflow is on the transfer's from-account, within $1 and ±2 days;
    /// it is backed by (strongest first) an existing transfer peer on the to-account, an inflow on the to-account (±$1, ±2 days of the
    /// outflow, not paired elsewhere), or — when the to-account has no bank feed, or the row names the to-account — a row that reads like a
    /// transfer. Best evidence first, then closest date, then closest amount.
    /// </summary>
    public static IReadOnlyList<PlannedTransferMatch> MatchTransfers(
        IEnumerable<PlannedTransfer> transfers,
        IReadOnlyList<PlanBankRow> outflows,
        IReadOnlyList<PlanBankRow> inflows)
    {
        var options = new List<(PlannedTransfer T, PlanBankRow Out, Guid? In, int Strength, int Days, long Diff)>();
        foreach (var t in transfers)
        {
            foreach (var x in outflows.Where(r => r.AccountId == t.FromAccountId && r.AmountCents < 0 && TransferMatches(t.AmountCents, t.DueDate, r.Magnitude, r.PostedOn)))
            {
                var days = Math.Abs(x.PostedOn.DayNumber - t.DueDate.DayNumber);
                var diff = Math.Abs(x.Magnitude - t.AmountCents);
                if (x.PeerId is { } peer)
                {
                    if (x.PeerAccountId == t.ToAccountId)
                    {
                        options.Add((t, x, peer, 3, days, diff));
                    }

                    continue;
                }

                foreach (var y in inflows.Where(r => r.AccountId == t.ToAccountId && r.AmountCents > 0
                             && Math.Abs(r.Magnitude - x.Magnitude) <= TransferToleranceCents
                             && Math.Abs(r.PostedOn.DayNumber - x.PostedOn.DayNumber) <= TransferWindowDays))
                {
                    options.Add((t, x, y.Id, 2, days + Math.Abs(y.PostedOn.DayNumber - x.PostedOn.DayNumber), diff));
                }

                if ((!t.ToLinked && x.LooksLikeTransfer) || NamesAccount(x.Text, t.ToName, t.ToInstitution))
                {
                    options.Add((t, x, null, 1, days, diff));
                }
            }
        }

        var usedTransfers = new HashSet<(Guid, DateOnly)>();
        var usedRows = new HashSet<Guid>();
        var result = new List<PlannedTransferMatch>();
        foreach (var o in options
                     .OrderByDescending(o => o.Strength)
                     .ThenBy(o => o.Days)
                     .ThenBy(o => o.Diff)
                     .ThenBy(o => o.T.DueDate)
                     .ThenBy(o => o.T.ItemId)
                     .ThenBy(o => o.Out.Id)
                     .ThenBy(o => o.In))
        {
            if (usedTransfers.Contains((o.T.ItemId, o.T.DueDate)) || usedRows.Contains(o.Out.Id) || (o.In is { } i && usedRows.Contains(i)))
            {
                continue;
            }

            usedTransfers.Add((o.T.ItemId, o.T.DueDate));
            usedRows.Add(o.Out.Id);
            if (o.In is { } inflow)
            {
                usedRows.Add(inflow);
            }

            result.Add(new PlannedTransferMatch(o.T.ItemId, o.T.DueDate, o.Out.Id, o.In, o.Out.Magnitude));
        }

        return result;
    }

    /// <summary>
    /// Does the bank text name the account? A significant word of its name or institution (≥ 3 letters, no digits, not a generic word like
    /// "checking", "savings", "bank") appears as a whole word: "ONLINE TRANSFER TO ALLY BANK" names "Ally Bank" accounts.
    /// </summary>
    public static bool NamesAccount(string? text, string name, string institution)
    {
        var haystack = $" {Fingerprint.Normalise(text)} ";
        if (haystack.Trim().Length == 0)
        {
            return false;
        }

        return $"{name} {institution}"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Fingerprint.Normalise)
            .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 3 && !t.Any(char.IsDigit) && !GenericAccountWords.Contains(t))
            .Any(t => haystack.Contains($" {t} ", StringComparison.Ordinal));
    }

    /// <summary>Two legs of one transfer: within 1 % (at least $1.00).</summary>
    private static long PairTolerance(long magnitude) => Math.Max(TransferToleranceCents, (long)Math.Round(magnitude * 0.01, MidpointRounding.AwayFromZero));
}
