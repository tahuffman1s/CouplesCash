using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;

namespace CouplesCash.Api.Services;

/// <summary>What the plan (and the sweep freeze, and the payday push) needs about a household at one moment.</summary>
public sealed class PlanContext
{
    public required HouseholdRow Household { get; init; }

    public required DateOnly Today { get; init; }

    public required IReadOnlyList<AccountRow> Accounts { get; init; }

    public required AccountRow? BillsAccount { get; init; }

    public required IReadOnlyDictionary<Guid, AccountRole> Roles { get; init; }

    public required IReadOnlyList<RecurringItemRow> Items { get; init; }

    /// <summary>Occurrences (DTO) due in the loaded range, sorted by date then name.</summary>
    public required IReadOnlyList<OccurrenceDto> Occurrences { get; init; }

    /// <summary>The same occurrences as the engine sees them (same order).</summary>
    public required IReadOnlyList<PlanOccurrence> PlanOccurrences { get; init; }

    /// <summary>The bills account's still-pending bank rows (any review status, not dropped / superseded), newest first.</summary>
    public required IReadOnlyList<ImportedTransactionRow> Pending { get; init; }

    public long PendingOutCents => Pending.Where(p => p.AmountCents < 0).Sum(p => -p.AmountCents);

    /// <summary>Available now: the bills account's bank available, else balance − pending outflows; before setup the v6 spendable sum.</summary>
    public long AvailableCents => BillsAccount is { } bills
        ? WeekEngine.BillsAvailable(bills.AvailableCents, bills.BalanceCents, PendingOutCents)
        : WeekEngine.SpendableV6(Accounts.Select(WeekService.ToSpendable));

    public Guid? BillsAccountId => BillsAccount?.Id;
}

/// <summary>
/// Composes <c>GET /v1/plan</c> (PLAN-V7 "The Friday plan"): loads the bills account, the pay week's occurrences, pending rows, the sweep and
/// the setup flags; all arithmetic is <see cref="PlanEngine"/>. "Today" is the household-local date.
/// </summary>
public sealed class PlanService(
    Npgsql.NpgsqlDataSource db,
    HouseholdRepository households,
    AccountRepository accounts,
    RecurringRepository items,
    OccurrenceService occurrences,
    ImportRepository imports,
    BankRepository banks,
    SweepRepository sweeps,
    TimeProvider time)
{
    /// <summary>Loads accounts, items and the occurrences due in [from, to] (and the bills account's pending rows).</summary>
    public async Task<PlanContext?> LoadAsync(Guid householdId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var household = await households.GetRowAsync(householdId, ct);
        if (household is null)
        {
            return null;
        }

        var today = WeekService.TodayFor(household, time);
        var accountRows = await accounts.ListAsync(householdId, ct);
        var bills = accountRows.FirstOrDefault(a => a.Role == AccountRole.Bills);
        var itemRows = await items.ListAsync(householdId, null, ct);
        var list = await occurrences.ListAsync(householdId, itemRows, from, to, ct, today);
        var byId = itemRows.ToDictionary(i => i.Id);
        var pending = bills is null ? [] : await imports.ListBankPendingAsync(householdId, bills.Id, null, reviewOrConfirmedOnly: false, ct);

        return new PlanContext
        {
            Household = household,
            Today = today,
            Accounts = accountRows,
            BillsAccount = bills,
            Roles = accountRows.ToDictionary(a => a.Id, a => a.Role),
            Items = itemRows,
            Occurrences = list,
            PlanOccurrences = list.Select(o => ToPlan(o, byId.GetValueOrDefault(o.ItemId))).ToList(),
            Pending = pending,
        };
    }

    public static PlanOccurrence ToPlan(OccurrenceDto o, RecurringItemRow? item) => new(
        o.ItemId,
        o.Kind,
        item?.Frequency ?? Frequency.Monthly,
        o.AmountCents,
        o.DueDate,
        o.Paid?.AmountCents,
        o.Paid?.Source == PaymentSource.Bank,
        o.AccountId,
        item?.FromAccountId,
        item?.ToAccountId);

    /// <summary>The pay week containing <paramref name="date"/> (default today). Null when the household row is missing.</summary>
    public async Task<PlanDto?> GetAsync(Guid householdId, DateOnly? date, CancellationToken ct)
    {
        var household = await households.GetRowAsync(householdId, ct);
        if (household is null)
        {
            return null;
        }

        var today = WeekService.TodayFor(household, time);
        var week = WeekEngine.WeekBounds(date ?? today, household.PaydayWeekday);
        var context = await LoadAsync(householdId, week.Start, week.End, ct);
        return context is null ? null : await BuildAsync(context, week, ct);
    }

    private async Task<PlanDto> BuildAsync(PlanContext c, WeekWindow week, CancellationToken ct)
    {
        var householdId = c.Household.Id;
        var today = c.Today;
        var currency = c.Household.Currency;
        var isCurrent = week.Contains(today);
        var billsId = c.BillsAccountId;

        var lines = c.Occurrences
            .Select((o, i) => (Dto: o, Plan: c.PlanOccurrences[i], Bucket: PlanEngine.Classify(c.PlanOccurrences[i], billsId, c.Roles)))
            .ToList();
        List<PlanLineDto> Lines(PlanBucket bucket) =>
            lines.Where(l => l.Bucket == bucket).Select(l => new PlanLineDto(PlanEngine.LineKind(l.Bucket, l.Dto.Kind), l.Dto)).ToList();

        var available = c.AvailableCents;
        var totals = PlanEngine.Totals(week, available, lines.Select(l => (l.Plan, l.Bucket)));
        var sweep = await sweeps.GetAsync(householdId, week.Start, ct);

        // This week's sweep, until it moves, is last week's money still in the bills account.
        var sweepOut = isCurrent && sweep is not null ? PlanEngine.SweepOut(sweep.PlannedCents, sweep.IsDone) : 0;

        // A past week shows the number that was frozen for the next payday's sweep (its "Left on Thursday"), when known.
        var left = totals.LeftAtEndCents - sweepOut;
        if (week.End < today && await sweeps.GetAsync(householdId, week.Start.AddDays(7), ct) is { } next)
        {
            left = next.LeftCents;
        }

        PlanBillsAccountDto? billsDto = null;
        await using var conn = await db.OpenConnectionAsync(ct);
        if (c.BillsAccount is { } bills)
        {
            var external = (await banks.ListExternalAccountsAsync(householdId, ct))
                .Where(e => e.AccountId == bills.Id)
                .OrderByDescending(e => e.UpdatedAt)
                .FirstOrDefault();
            billsDto = new PlanBillsAccountDto(
                bills.Id,
                bills.Name,
                available,
                bills.BalanceCents,
                bills.AvailableCents is not null,
                c.Pending.Select(p => p.ToDto()).ToList(),
                c.PendingOutCents,
                MappingV3.ParseFeed(external?.LastFeed));
        }

        var checklist = new List<ChecklistItemDto>();
        if (isCurrent)
        {
            foreach (var l in lines.Where(l => PlanEngine.IsPaycheck(l.Plan, l.Bucket, week)))
            {
                var row = l.Dto.Paid is null ? null : await imports.FindPayingRowAsync(conn, householdId, l.Dto.ItemId, l.Dto.DueDate, ct);
                checklist.Add(new ChecklistItemDto("paycheck", $"Paycheck arrived · {Money.Format(l.Dto.Paid?.AmountCents ?? l.Dto.AmountCents, currency)}",
                    l.Dto.Paid?.AmountCents ?? l.Dto.AmountCents, l.Dto.Paid is not null, l.Dto.Paid?.Auto ?? false, row?.Id, l.Dto.ItemId, l.Dto.DueDate));
            }

            foreach (var l in lines.Where(l => PlanEngine.IsChecklistFun(l.Plan, l.Bucket, week)))
            {
                var row = l.Dto.Paid is null ? null : await imports.FindPayingRowAsync(conn, householdId, l.Dto.ItemId, l.Dto.DueDate, ct);
                var to = l.Plan.ToAccountId is { } toId ? c.Accounts.FirstOrDefault(a => a.Id == toId) : null;
                var toLabel = to is null ? l.Dto.Name
                    : to.OwnerName is { } owner && !to.Name.Contains(owner, StringComparison.OrdinalIgnoreCase) ? $"{to.Name} ({owner})" : to.Name;
                checklist.Add(new ChecklistItemDto("fun", $"{Money.Format(l.Dto.AmountCents, currency)} → {toLabel}", l.Dto.AmountCents, l.Dto.Paid is not null,
                    l.Dto.Paid?.Auto ?? false, row?.Id, l.Dto.ItemId, l.Dto.DueDate));
            }

            if (sweep is not null)
            {
                checklist.Add(sweep.PlannedCents > 0
                    ? new ChecklistItemDto("sweep", $"Move {Money.Format(sweep.PlannedCents, currency)} to savings", sweep.PlannedCents, sweep.IsDone, sweep.IsAuto,
                        sweep.TransactionId, null, week.Start)
                    : new ChecklistItemDto("sweep", $"Nothing to move this week ({Money.Format(sweep.LeftCents, currency)})", 0, true, false, null, null, week.Start));
            }

            if (!PlanEngine.ChecklistVisible(week, today, checklist.All(i => i.Done)))
            {
                checklist.Clear();
            }
        }

        var (_, linkCount) = await banks.SyncSummaryAsync(householdId, ct);
        var members = await households.MemberCountAsync(householdId, ct);
        var setup = new PlanSetupDto(
            BanksLinked: linkCount > 0,
            RolesSet: c.BillsAccount is not null,
            PaycheckKnown: c.Items.Any(i => i.Kind == Kind.Income),
            BillsAdded: c.Items.Any(i => i.Kind.IsOutflow()),
            PartnerJoined: members >= 2);

        var strip = c.Accounts
            .OrderBy(a => a.Role switch { AccountRole.Bills => 0, AccountRole.Savings => 1, AccountRole.Fun => 2, _ => 3 })
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new PlanAccountDto(a.Id, a.Name, a.Role, a.OwnerName, a.BalanceCents, a.AvailableCents))
            .ToList();

        return new PlanDto(
            Start: week.Start,
            End: week.End,
            NextPayday: week.Start.AddDays(7),
            IsCurrent: isCurrent,
            IsPayday: PlanEngine.IsPayday(today, c.Household.PaydayWeekday),
            Today: today,
            BillsAccount: billsDto,
            Accounts: strip,
            Bills: Lines(PlanBucket.Bill),
            FunMoney: Lines(PlanBucket.FunMoney),
            OneOffs: Lines(PlanBucket.OneOff),
            Income: Lines(PlanBucket.Income),
            BillsOutCents: totals.BillsOutCents,
            FunOutCents: totals.FunOutCents,
            OneOffOutCents: totals.OneOffOutCents,
            IncomeInCents: totals.IncomeInCents,
            SweepOutCents: sweepOut,
            LeftAtEndCents: left,
            SavingsBills: Lines(PlanBucket.SavingsBill),
            Checklist: checklist,
            Sweep: sweep?.ToDto(),
            ToReviewCount: await imports.CountPendingAsync(conn, householdId, ct),
            Setup: setup);
    }
}

public static class MappingV7
{
    public static SweepDto ToDto(this SweepRow s) => new(s.WeekStart, s.PlannedCents, s.ActualCents, s.DoneAt, s.IsAuto, s.LeftCents, s.TransactionId);
}
