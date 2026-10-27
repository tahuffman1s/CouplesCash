using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using Npgsql;

namespace CouplesCash.Api.Services;

/// <summary>What one bank-feed pass of the Friday routine did.</summary>
public readonly record struct FridayPassResult(int PlannedTransfersPaid, int SweepsDone)
{
    public int Total => PlannedTransfersPaid + SweepsDone;
}

/// <summary>The outcome of a manual sweep tick / untick. <see cref="NotFound"/> = the week has no frozen sweep.</summary>
public sealed record SweepOutcome(SweepRow? Sweep, bool NotFound = false);

/// <summary>
/// v7 automation around the Friday plan (PLAN-V7 "Recurring transfers", "Savings sweep"):
/// <list type="bullet">
/// <item><b>Bank-feed pass</b> (<see cref="RunAsync"/>, inside the caller's unit of work, before the matcher scores new rows): unpaid planned
/// transfers (fun money) are paid from the bank rows that moved them (<c>transfer.planned_paid</c>), and the week's savings sweep is ticked
/// from the bills → savings transfer (<c>sweep.auto</c>). The rows stay <c>ignored / transfer</c>; transfers never count as spending.</item>
/// <item><b>Freeze</b> (<see cref="FreezeDueAsync"/>, scheduler): at payday 00:00 household time the sweep's planned amount is frozen from
/// the previous pay week's left-at-end; idempotent per (household, week_start).</item>
/// <item><b>Manual tick</b> (<see cref="MarkSweepDoneAsync"/> / <see cref="UndoSweepAsync"/>).</item>
/// </list>
/// Selection logic is <see cref="PlanEngine"/>; this class does the I/O. Logs under a "Scheduler"-free category except when the scheduler runs it.
/// </summary>
public sealed class FridayService(
    NpgsqlDataSource db,
    HouseholdRepository households,
    AccountRepository accounts,
    RecurringRepository items,
    ImportRepository imports,
    SweepRepository sweeps,
    PlanService plans,
    ActivityService activity,
    TimeProvider time,
    ILogger<FridayService> logger)
{
    /// <summary>Planned-transfer occurrences this far back are still matched (a slow bank, an API that was down).</summary>
    public const int LookbackDays = 14;

    // ---------------------------------------------------------------------------------------------
    // Bank-feed pass
    // ---------------------------------------------------------------------------------------------

    /// <summary>Pays planned transfers and ticks the sweep from the bank rows visible in <paramref name="uow"/>'s transaction.</summary>
    public async Task<FridayPassResult> RunAsync(UnitOfWork uow, Guid householdId, CancellationToken ct)
    {
        var conn = uow.Connection;
        if (await households.GetRowAsync(conn, householdId, ct) is not { } household)
        {
            return default;
        }

        var accountRows = await accounts.ListAsync(conn, householdId, ct);
        var transferItems = (await items.ListAsync(conn, householdId, Kind.Transfer, ct))
            .Where(i => i.FromAccountId is not null && i.ToAccountId is not null)
            .ToList();
        var bills = accountRows.FirstOrDefault(a => a.Role == AccountRole.Bills);
        if (transferItems.Count == 0 && bills is null)
        {
            return default;
        }

        var today = DateOnly.FromDateTime(SchedulerRules.ToLocal(time.Now(), household.Timezone));
        var paid = await PayPlannedTransfersAsync(uow, household, accountRows, transferItems, today, ct);
        var swept = bills is null ? 0 : await DetectSweepAsync(uow, household, accountRows, bills, today, ct);
        await CompletePairsAsync(uow, household, accountRows, transferItems, bills, today, ct);
        return new FridayPassResult(paid, swept);
    }

    private async Task<int> PayPlannedTransfersAsync(UnitOfWork uow, HouseholdRow household, IReadOnlyList<AccountRow> accountRows, IReadOnlyList<RecurringItemRow> transferItems, DateOnly today, CancellationToken ct)
    {
        if (transferItems.Count == 0)
        {
            return 0;
        }

        var conn = uow.Connection;
        var householdId = household.Id;
        var from = today.AddDays(-LookbackDays);
        var to = today.AddDays(PlanEngine.TransferWindowDays);
        var paidSet = (await items.ListPaymentsAsync(conn, householdId, from, to, ct))
            .Where(p => p.Source == PaymentSource.Bank).Select(p => (p.RecurringItemId, p.DueDate)).ToHashSet();
        var byId = accountRows.ToDictionary(a => a.Id);
        var planned = transferItems
            .SelectMany(i => RecurrenceEngine.Expand(i.Rule, from, to).Where(d => !paidSet.Contains((i.Id, d))).Select(d => (Item: i, Due: d)))
            .Where(x => byId.ContainsKey(x.Item.FromAccountId!.Value) && byId.ContainsKey(x.Item.ToAccountId!.Value))
            .Select(x =>
            {
                var target = byId[x.Item.ToAccountId!.Value];
                return new PlannedTransfer(x.Item.Id, x.Due, x.Item.AmountCents, x.Item.FromAccountId!.Value, target.Id, target.Linked, target.Name, target.Institution);
            })
            .ToList();
        if (planned.Count == 0)
        {
            return 0;
        }

        var (outflows, inflows) = await CandidateRowsAsync(conn, householdId, from.AddDays(-PlanEngine.TransferWindowDays), to.AddDays(PlanEngine.TransferWindowDays), ct);
        var matches = PlanEngine.MatchTransfers(planned, outflows, inflows);
        var itemsById = transferItems.ToDictionary(i => i.Id);
        var now = time.Now();
        var count = 0;
        foreach (var m in matches)
        {
            var item = itemsById[m.ItemId];
            await imports.LockAsync(conn, householdId, m.OutflowId, ct);
            var outRow = await imports.GetAsync(conn, householdId, m.OutflowId, ct);
            if (outRow is null || !Claimable(outRow) || await items.PayFromBankAsync(conn, item.Id, m.DueDate, m.AmountCents, now, ct) is null)
            {
                continue;
            }

            await MarkPairAsync(conn, householdId, outRow, m.InflowId, now, ct);
            await imports.SetPlannedLinkAsync(conn, householdId, outRow.Id, item.Id, m.DueDate, ct);
            await activity.RecordAsync(conn, uow.Transaction, householdId, null, "transfer.planned_paid", "transfer", outRow.Id,
                ActivitySummaries.TransferPlannedPaid(item.Name, m.AmountCents, household.Currency),
                new { itemId = item.Id, dueDate = m.DueDate, transactionId = outRow.Id, peerId = m.InflowId, amountCents = m.AmountCents, plannedCents = item.AmountCents }, ct);
            count++;
        }

        return count;
    }

    /// <summary>This week's sweep (made on its last day) and last week's (its move may post over the weekend), each looked for around its sweep day.</summary>
    private async Task<int> DetectSweepAsync(UnitOfWork uow, HouseholdRow household, IReadOnlyList<AccountRow> accountRows, AccountRow bills, DateOnly today, CancellationToken ct)
    {
        var week = WeekEngine.WeekBounds(today, household.PaydayWeekday);
        var done = 0;
        foreach (var w in new[] { week, week.Shift(-1) })
        {
            done += await DetectSweepAsync(uow, household, accountRows, bills, w, ct);
        }

        return done;
    }

    private async Task<int> DetectSweepAsync(UnitOfWork uow, HouseholdRow household, IReadOnlyList<AccountRow> accountRows, AccountRow bills, WeekWindow week, CancellationToken ct)
    {
        var conn = uow.Connection;
        var householdId = household.Id;
        var sweepDay = PlanEngine.SweepDay(week);
        var sweep = await sweeps.LockAsync(conn, householdId, week.Start, ct);
        if (sweep is null || sweep.PlannedCents <= 0 || sweep.AutoBlocked || sweep.TransactionId is not null)
        {
            return 0;
        }

        var savings = accountRows.Where(a => a.Role == AccountRole.Savings).ToList();
        if (savings.Count == 0)
        {
            return 0;
        }

        var (outflows, inflows) = await CandidateRowsAsync(conn, householdId, sweepDay.AddDays(-PlanEngine.SweepDaysBefore - PlanEngine.SweepPairDays),
            sweepDay.AddDays(PlanEngine.SweepDaysAfter + PlanEngine.SweepPairDays), ct);
        var match = PlanEngine.FindSweep(sweep.PlannedCents, sweepDay, bills.Id, savings.Select(a => a.Id).ToList(), savings.Any(a => a.Linked),
            savings.Select(a => (a.Name, a.Institution)).ToList(), outflows, inflows);
        if (match is null)
        {
            return 0;
        }

        await imports.LockAsync(conn, householdId, match.OutflowId, ct);
        var outRow = await imports.GetAsync(conn, householdId, match.OutflowId, ct);
        if (outRow is null || !Claimable(outRow))
        {
            return 0;
        }

        var now = time.Now();
        await MarkPairAsync(conn, householdId, outRow, match.InflowId, now, ct);
        if (sweep.IsDone)
        {
            await sweeps.AttachTransactionAsync(conn, householdId, week.Start, outRow.Id, ct);
        }
        else
        {
            await sweeps.MarkDoneAsync(conn, householdId, week.Start, match.AmountCents, doneBy: null, outRow.Id, now, ct);
        }

        await activity.RecordAsync(conn, uow.Transaction, householdId, null, "sweep.auto", "sweep", sweep.Id,
            ActivitySummaries.SweepAuto(match.AmountCents, household.Currency),
            new { weekStart = week.Start, plannedCents = sweep.PlannedCents, actualCents = match.AmountCents, transactionId = outRow.Id, peerId = match.InflowId, attachedToManualTick = sweep.IsDone }, ct);
        return 1;
    }

    /// <summary>
    /// A planned transfer or sweep matched one-sided (the other bank feed was slow): when the destination's inflow shows up later it is
    /// paired with the outflow, so it never waits in To review.
    /// </summary>
    private async Task CompletePairsAsync(UnitOfWork uow, HouseholdRow household, IReadOnlyList<AccountRow> accountRows, IReadOnlyList<RecurringItemRow> transferItems, AccountRow? bills, DateOnly today, CancellationToken ct)
    {
        var conn = uow.Connection;
        var householdId = household.Id;
        var from = today.AddDays(-LookbackDays - PlanEngine.TransferWindowDays);
        var to = today.AddDays(PlanEngine.TransferWindowDays);
        var rows = await imports.ListVisibleInWindowAsync(conn, householdId, from, to, ct);
        var attached = await sweeps.AttachedTransactionIdsAsync(conn, householdId, ct);
        var itemsById = transferItems.ToDictionary(i => i.Id);
        var savingsIds = accountRows.Where(a => a.Role == AccountRole.Savings).Select(a => a.Id).ToHashSet();
        var used = new HashSet<Guid>();
        var now = time.Now();

        foreach (var outRow in rows.Where(r => r is { IsTransfer: true, TransferPeerId: null, AmountCents: < 0 }))
        {
            IReadOnlySet<Guid> destinations = outRow.ConfirmedItemId is { } itemId && itemsById.TryGetValue(itemId, out var item) && item.ToAccountId is { } toId
                ? new HashSet<Guid> { toId }
                : attached.Contains(outRow.Id) && bills is not null && outRow.AccountId == bills.Id ? savingsIds : new HashSet<Guid>();
            if (destinations.Count == 0)
            {
                continue;
            }

            var inflow = rows
                .Where(r => r.AmountCents > 0 && r.AccountId is { } a && destinations.Contains(a) && !used.Contains(r.Id) && IsFreeInflow(r))
                .Where(r => Math.Abs(r.Magnitude - outRow.Magnitude) <= PlanEngine.TransferToleranceCents
                            && Math.Abs(r.PostedOn.DayNumber - outRow.PostedOn.DayNumber) <= PlanEngine.SweepPairDays)
                .OrderBy(r => Math.Abs(r.PostedOn.DayNumber - outRow.PostedOn.DayNumber)).ThenBy(r => Math.Abs(r.Magnitude - outRow.Magnitude)).ThenBy(r => r.Id)
                .FirstOrDefault();
            if (inflow is null)
            {
                continue;
            }

            used.Add(inflow.Id);
            await imports.LockAsync(conn, householdId, inflow.Id, ct);
            await imports.MarkTransferAsync(conn, householdId, inflow.Id, outRow.Id, auto: true, reviewer: null, now, ct);
            await imports.SetTransferPeerAsync(conn, householdId, outRow.Id, inflow.Id, ct);
        }
    }

    /// <summary>
    /// The bills-side rows a pass may claim (to review, or a transfer not yet linked to a plan, not blocked, not attached to a sweep) and the
    /// destination-side inflows that may pair with them (unpaired; to review, nothing attached, or a one-sided transfer).
    /// </summary>
    private async Task<(IReadOnlyList<PlanBankRow> Outflows, IReadOnlyList<PlanBankRow> Inflows)> CandidateRowsAsync(NpgsqlConnection conn, Guid householdId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await imports.ListVisibleInWindowAsync(conn, householdId, from, to, ct);
        var attached = await sweeps.AttachedTransactionIdsAsync(conn, householdId, ct);
        var transferPayees = (await imports.ListRulesAsync(conn, householdId, ct)).Where(r => r.IsTransfer).Select(r => r.PayeeNorm).ToHashSet(StringComparer.Ordinal);

        var outflows = rows
            .Where(r => r.AmountCents < 0 && Claimable(r) && !attached.Contains(r.Id))
            .Select(r => new PlanBankRow(r.Id, r.AccountId, r.PostedOn, r.AmountCents, r.IsTransfer ? r.TransferPeerId : null, r.IsTransfer ? r.PeerAccountId : null,
                r.SuggestedTransfer || r.IsTransfer || TransactionMatcher.IsTransferKeyword(r.Description, r.Payee, isOutflow: true)
                    || transferPayees.Contains(TransactionMatcher.NormalisePayee(r.Payee, r.Description)),
                $"{r.Description} {r.Payee}"))
            .ToList();
        var inflows = rows
            .Where(r => r.AmountCents > 0 && IsFreeInflow(r))
            .Select(r => new PlanBankRow(r.Id, r.AccountId, r.PostedOn, r.AmountCents, null, null, false, $"{r.Description} {r.Payee}"))
            .ToList();
        return (outflows, inflows);
    }

    /// <summary>A bills-side row the pass may claim: still to review, or a transfer that no plan has claimed; never one a person sent back.</summary>
    private static bool Claimable(ImportedTransactionRow r) =>
        !r.AutoBlocked && !r.IsHidden && (r.IsPending || (r.IsTransfer && r.ConfirmedItemId is null));

    /// <summary>An inflow that can become the other leg: unpaired and either to review, confirmed with nothing attached (fun money), or one-sided.</summary>
    private static bool IsFreeInflow(ImportedTransactionRow r) =>
        !r.IsHidden && r.TransferPeerId is null && !r.AutoBlocked
        && (r.IsPending || (r.Status == "confirmed" && r.ConfirmedExpenseId is null && r.ConfirmedItemId is null) || r.IsTransfer);

    /// <summary>The outflow (and its inflow, when seen) become <c>ignored / transfer</c>, auto, peers linked.</summary>
    private async Task MarkPairAsync(NpgsqlConnection conn, Guid householdId, ImportedTransactionRow outRow, Guid? inflowId, DateTime now, CancellationToken ct)
    {
        var peer = inflowId ?? (outRow.IsTransfer ? outRow.TransferPeerId : null);
        if (!(outRow.IsTransfer && outRow.TransferPeerId == peer))
        {
            await imports.MarkTransferAsync(conn, householdId, outRow.Id, peer, auto: true, reviewer: null, now, ct);
        }

        if (inflowId is { } inflow)
        {
            await imports.LockAsync(conn, householdId, inflow, ct);
            if (await imports.GetAsync(conn, householdId, inflow, ct) is { } inRow && !(inRow.IsTransfer && inRow.TransferPeerId == outRow.Id))
            {
                await imports.MarkTransferAsync(conn, householdId, inflow, outRow.Id, auto: true, reviewer: null, now, ct);
            }
        }
    }

    /// <summary>Scheduler phase: runs the bank-feed pass for every household with a bills account or a planned transfer, each in its own transaction.</summary>
    public async Task RunAllAsync(CancellationToken ct)
    {
        foreach (var household in await households.ListAllAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var uow = await db.BeginUnitOfWorkAsync(ct);
                var result = await RunAsync(uow, household.Id, ct);
                await uow.CommitAsync(ct);
                if (result.Total > 0)
                {
                    logger.LogInformation("Scheduler friday pass of household {HouseholdId}: {Transfers} planned transfer(s) paid, {Sweeps} sweep(s) ticked",
                        household.Id, result.PlannedTransfersPaid, result.SweepsDone);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduler friday pass of household {HouseholdId} failed", household.Id);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Freeze
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Scheduler phase: on the sweep day — the last day of the pay week, household-local — every household with a bills account gets that
    /// week's sweep, frozen from the week's left-at-end: what is left on Thursday moves to savings on Thursday. Idempotent: the unique
    /// (household, week_start) keeps the first value.
    /// </summary>
    public async Task FreezeDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        foreach (var household in await households.ListAllAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var today = DateOnly.FromDateTime(SchedulerRules.ToLocal(nowUtc, household.Timezone));
            if (PlanEngine.SweepWeekToFreeze(today, household.PaydayWeekday) is not { } week
                || await sweeps.GetAsync(household.Id, week.Start, ct) is not null)
            {
                continue;
            }

            try
            {
                await FreezeAsync(household, week, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduler sweep freeze of household {HouseholdId} failed", household.Id);
            }
        }
    }

    private async Task FreezeAsync(HouseholdRow household, WeekWindow week, CancellationToken ct)
    {
        var context = await plans.LoadAsync(household.Id, week.Start, week.End, ct);
        if (context?.BillsAccount is null)
        {
            return;
        }

        // The week's "Left on Thursday", on Thursday.
        var left = PlanEngine.Totals(week, context.AvailableCents, context.PlanOccurrences, context.BillsAccountId, context.Roles).LeftAtEndCents;
        var plannedCents = PlanEngine.SweepPlanned(left);
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await sweeps.InsertAsync(uow.Connection, household.Id, week.Start, plannedCents, left, time.Now(), ct);
        if (row is null)
        {
            return;
        }

        await activity.RecordAsync(uow.Connection, uow.Transaction, household.Id, null, "sweep.planned", "sweep", row.Id,
            ActivitySummaries.SweepPlanned(plannedCents, left, household.Currency),
            new { weekStart = week.Start, plannedCents, leftCents = left }, ct);
        await uow.CommitAsync(ct);
        logger.LogInformation("Scheduler sweep frozen for household {HouseholdId} week {WeekStart}: planned {Planned} (left {Left})", household.Id, week.Start, plannedCents, left);
    }

    // ---------------------------------------------------------------------------------------------
    // Manual tick / untick
    // ---------------------------------------------------------------------------------------------

    /// <summary><c>POST /plan/sweep/done</c>: ticks the sweep (amount = <paramref name="actualCents"/> ?? planned). Ticking it again updates the amount.</summary>
    public async Task<SweepOutcome> MarkSweepDoneAsync(Guid householdId, Guid userId, string? actorName, DateOnly weekStart, long? actualCents, CancellationToken ct)
    {
        var currency = await households.CurrencyAsync(householdId, ct);
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var sweep = await sweeps.LockAsync(uow.Connection, householdId, weekStart, ct);
        if (sweep is null)
        {
            return new SweepOutcome(null, NotFound: true);
        }

        var amount = actualCents ?? sweep.ActualCents ?? sweep.PlannedCents;
        var updated = await sweeps.MarkDoneAsync(uow.Connection, householdId, weekStart, amount, userId, transactionId: null, time.Now(), ct);
        await activity.RecordAsync(uow, householdId, userId, "sweep.done", "sweep", sweep.Id, ActivitySummaries.SweepDone(amount, currency),
            new { weekStart, plannedCents = sweep.PlannedCents, actualCents = amount, done = true }, ct, actorName);
        await uow.CommitAsync(ct);
        return new SweepOutcome(updated);
    }

    /// <summary><c>DELETE /plan/sweep/done</c>: unticks it; the bank-feed detection then leaves that week alone.</summary>
    public async Task<SweepOutcome> UndoSweepAsync(Guid householdId, Guid userId, string? actorName, DateOnly weekStart, CancellationToken ct)
    {
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var sweep = await sweeps.LockAsync(uow.Connection, householdId, weekStart, ct);
        if (sweep is null)
        {
            return new SweepOutcome(null, NotFound: true);
        }

        var updated = await sweeps.UndoAsync(uow.Connection, householdId, weekStart, ct);
        await activity.RecordAsync(uow, householdId, userId, "sweep.done", "sweep", sweep.Id, ActivitySummaries.SweepUndone(),
            new { weekStart, plannedCents = sweep.PlannedCents, done = false, wasAuto = sweep.IsAuto, transactionId = sweep.TransactionId }, ct, actorName);
        await uow.CommitAsync(ct);
        return new SweepOutcome(updated);
    }

    // ---------------------------------------------------------------------------------------------
    // Payday push
    // ---------------------------------------------------------------------------------------------

    /// <summary>The payday push body for a household on <paramref name="today"/> (its payday): sweep amount and fun money (values only).</summary>
    public async Task<string?> PaydayBodyAsync(Guid householdId, CancellationToken ct)
    {
        var household = await households.GetRowAsync(householdId, ct);
        if (household is null)
        {
            return null;
        }

        var today = WeekService.TodayFor(household, time);
        var week = WeekEngine.WeekBounds(today, household.PaydayWeekday);
        var context = await plans.LoadAsync(householdId, week.Start, week.End, ct);
        if (context is null)
        {
            return null;
        }

        var fun = context.PlanOccurrences
            .Where(o => PlanEngine.IsChecklistFun(o, PlanEngine.Classify(o, context.BillsAccountId, context.Roles), week))
            .ToList();
        return SchedulerRules.PaydayBody(fun.Sum(o => o.AmountCents), fun.Count > 0 && fun.All(o => o.IsPaid), household.Currency);
    }

    /// <summary>
    /// The sweep-day push body ("Move $253.17 to savings today"), or null when the sweep is not frozen yet, is zero or is already done.
    /// <paramref name="week"/> is the pay week ending today.
    /// </summary>
    public async Task<string?> SweepBodyAsync(Guid householdId, WeekWindow week, CancellationToken ct)
    {
        var sweep = await sweeps.GetAsync(householdId, week.Start, ct);
        if (sweep is not { PlannedCents: > 0, IsDone: false })
        {
            return null;
        }

        return SchedulerRules.SweepBody(sweep.PlannedCents, await households.CurrencyAsync(householdId, ct));
    }
}
