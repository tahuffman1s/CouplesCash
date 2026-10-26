using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using Npgsql;

namespace CouplesCash.Api.Services;

/// <summary>
/// A field-level reason a review action was refused (becomes a 400 <c>errors: { field: [message] }</c>), or a 409 when
/// <paramref name="Conflict"/> (v5: the occurrence is already paid by something else).
/// </summary>
public sealed record InboxError(string Field, string Message, bool Conflict = false);

/// <summary>v5: the outcome of the matcher over a batch of new rows.</summary>
public sealed record SuggestResult(IReadOnlyDictionary<Guid, Suggestion> Suggestions, IReadOnlyList<(Guid A, Guid B)> TransferPairs)
{
    public static readonly SuggestResult Empty = new(new Dictionary<Guid, Suggestion>(), []);
}

/// <summary>What <see cref="InboxService.ReverseAsync"/> undid.</summary>
public sealed record Reversal(bool HadExpense, bool HadPayment, Guid? PeerId, bool WasTransfer);

/// <summary>
/// The review inbox: auto-ignores transfers (v5; suggestions and automatic matches come from <c>MatchingService</c>, v6), learns from
/// confirmed matches (payee rules, v6 learned bank names), and applies the confirm / ignore / restore / match / transfer / unmatch semantics of PLAN-V3 and
/// PLAN-V5 inside the caller's <see cref="UnitOfWork"/>.
/// </summary>
public sealed class InboxService(
    ImportRepository imports,
    RecurringRepository items,
    OccurrenceService occurrences,
    CategoryRepository categories,
    ExpenseRepository expenses,
    AccountRepository accounts,
    HouseholdRepository households,
    ActivityService activity,
    TimeProvider time)
{
    public const int MaxExpenseNameLength = 80;

    /// <summary>At most this many ranked candidates are returned.</summary>
    public const int MaxCandidates = 20;

    // ---------------------------------------------------------------------------------------------
    // Automatic actions (suggestions themselves are computed by MatchingService, v6)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// v5: with <c>autoIgnoreTransfers</c> on (v6: and the auto-match level not <c>off</c>), every detected pair (both rows still to review,
    /// neither matched to a bill/income) becomes <c>ignored / transfer</c> with peers set and <c>transfer_auto</c>; rows suggested by a
    /// learned transfer payee (85) are ignored as one-sided transfers. One <c>transfer.auto</c> event per batch carries the count. Returns
    /// how many rows were ignored.
    /// </summary>
    public async Task<int> AutoIgnoreTransfersAsync(UnitOfWork uow, Guid householdId, IReadOnlyList<ImportedTransactionRow> rows, SuggestResult result, CancellationToken ct)
    {
        if (rows.Count == 0
            || await households.GetRowAsync(uow.Connection, householdId, ct) is not { } household
            || !MatchingRules.AutoIgnoresTransfers(household.AutoMatchLevel, household.AutoIgnoreTransfers))
        {
            return 0;
        }

        var now = time.Now();
        var ignored = new List<Guid>();
        var pairsDone = 0;
        foreach (var (a, b) in result.TransferPairs)
        {
            if (result.Suggestions.TryGetValue(a, out var sa) && sa.Kind == SuggestionKind.Occurrence
                || result.Suggestions.TryGetValue(b, out var sb) && sb.Kind == SuggestionKind.Occurrence)
            {
                continue;
            }

            await imports.LockAsync(uow.Connection, householdId, a, ct);
            await imports.LockAsync(uow.Connection, householdId, b, ct);
            var rowA = await imports.GetAsync(uow.Connection, householdId, a, ct);
            var rowB = await imports.GetAsync(uow.Connection, householdId, b, ct);
            if (rowA is not { IsPending: true } || rowB is not { IsPending: true } || rowA.SuggestedItemId is not null || rowB.SuggestedItemId is not null)
            {
                continue;
            }

            await imports.MarkTransferAsync(uow.Connection, householdId, a, b, auto: true, reviewer: null, now, ct);
            await imports.MarkTransferAsync(uow.Connection, householdId, b, a, auto: true, reviewer: null, now, ct);
            ignored.Add(a);
            ignored.Add(b);
            pairsDone++;
        }

        var oneSided = 0;
        foreach (var row in rows)
        {
            if (ignored.Contains(row.Id)
                || !result.Suggestions.TryGetValue(row.Id, out var s)
                || s is not { Kind: SuggestionKind.Transfer, Confidence: TransactionMatcher.TransferRuleConfidence })
            {
                continue;
            }

            await imports.MarkTransferAsync(uow.Connection, householdId, row.Id, null, auto: true, reviewer: null, now, ct);
            ignored.Add(row.Id);
            oneSided++;
        }

        if (ignored.Count > 0)
        {
            await activity.RecordAsync(uow.Connection, uow.Transaction, householdId, null, "transfer.auto", "transfer", null,
                ActivitySummaries.TransferAuto(ignored.Count), new { count = ignored.Count, pairs = pairsDone, oneSided, transactionIds = ignored }, ct);
        }

        return ignored.Count;
    }

    // ---------------------------------------------------------------------------------------------
    // Review actions (caller holds the row lock and passes the current row)
    // ---------------------------------------------------------------------------------------------

    public async Task<InboxError?> ConfirmExpenseAsync(UnitOfWork uow, Guid householdId, Guid userId, string? actorName, string currency, ImportedTransactionRow row, Guid? categoryId, string? name, bool quiet, CancellationToken ct)
    {
        if (!row.IsPending)
        {
            return new InboxError("status", "Only pending transactions can be confirmed.");
        }

        if (!row.IsOutflow)
        {
            return new InboxError("as", "Money in cannot be recorded as an expense; match it to an income item or ignore it.");
        }

        var category = categoryId ?? (row.SuggestedItemId is null ? row.SuggestedCategoryId : null);
        if (category is { } c && !await categories.ExistsAsync(uow.Connection, householdId, c, ct))
        {
            return new InboxError("categoryId", "Unknown category.");
        }

        Guid? accountId = row.AccountId is { } a && await accounts.ExistsAsync(uow.Connection, householdId, a, ct) ? a : null;
        var expenseName = ExpenseName(row, name);

        var now = time.Now();
        var expense = await expenses.CreateAsync(uow.Connection, householdId, userId,
            new ExpenseFields(expenseName, row.Magnitude, category, accountId, row.PostedOn, row.Memo, Deducted: false), now, ct);
        await imports.ConfirmExpenseAsync(uow.Connection, householdId, row.Id, expense.Id, userId, now, ct);

        if (category is { } learned)
        {
            await LearnRuleAsync(uow.Connection, householdId, row, learned, null, now, ct);
        }

        if (!quiet)
        {
            await activity.RecordAsync(uow, householdId, userId, "transaction.confirmed", "transaction", row.Id,
                ActivitySummaries.TransactionConfirmedExpense(expenseName, row.Magnitude, currency),
                new { transactionId = row.Id, expenseId = expense.Id, categoryId = category, amountCents = row.Magnitude, @as = "expense" }, ct, actorName);
        }

        return null;
    }

    public async Task<InboxError?> ConfirmOccurrenceAsync(UnitOfWork uow, Guid householdId, Guid userId, string? actorName, string currency, ImportedTransactionRow row, Guid itemId, DateOnly dueDate, bool quiet, CancellationToken ct)
    {
        if (!row.IsPending)
        {
            return new InboxError("status", "Only pending transactions can be confirmed.");
        }

        var (item, error) = await CheckOccurrenceAsync(uow.Connection, householdId, row, itemId, dueDate, ct);
        if (error is not null)
        {
            return error;
        }

        if (await items.GetPaymentAsync(uow.Connection, item!.Id, dueDate, ct) is not null)
        {
            return new InboxError("dueDate", "This occurrence is already paid.");
        }

        await PayAsync(uow, householdId, userId, row, item, dueDate, ct);
        if (!quiet)
        {
            await activity.RecordAsync(uow, householdId, userId, "transaction.confirmed", "transaction", row.Id,
                ActivitySummaries.TransactionConfirmedOccurrence(item.Name, row.Magnitude, currency),
                new { transactionId = row.Id, itemId = item.Id, dueDate, amountCents = row.Magnitude, @as = "occurrence" }, ct, actorName);
        }

        return null;
    }

    /// <summary>
    /// v5 "Match to a bill or income" from any status: validates first (sign ↔ kind, real due date, 409 when the occurrence is paid
    /// by another transaction or by hand), then reverses whatever the row stood for (expense, other payment, transfer + peer) and pays
    /// the occurrence with the bank amount. Learns payee → item. Matching to the occurrence it already pays is a no-op.
    /// </summary>
    public async Task<InboxError?> MatchAsync(UnitOfWork uow, Guid householdId, Guid userId, string? actorName, string currency, ImportedTransactionRow row, Guid itemId, DateOnly dueDate, bool quiet, CancellationToken ct)
    {
        if (row.IsHidden)
        {
            return new InboxError("status", "This authorization was dropped or superseded by the bank; restore it first.");
        }

        var (item, error) = await CheckOccurrenceAsync(uow.Connection, householdId, row, itemId, dueDate, ct);
        if (error is not null)
        {
            return error;
        }

        var paysIt = row is { Status: "confirmed", ConfirmedItemId: { } ci, ConfirmedDueDate: { } cd } && ci == item!.Id && cd == dueDate;
        if (paysIt)
        {
            return null;
        }

        if (await items.GetPaymentAsync(uow.Connection, item!.Id, dueDate, ct) is { } payment)
        {
            var other = await imports.FindConfirmedForOccurrenceAsync(uow.Connection, householdId, item.Id, dueDate, ct);
            return new InboxError("dueDate", other is not null
                ? "This occurrence is already paid by another transaction; unmatch that one first."
                : payment.Source == PaymentSource.Autopay ? "This occurrence was auto-paid; mark it unpaid first." : "This occurrence was marked paid by hand; mark it unpaid first.",
                Conflict: true);
        }

        var reversal = await ReverseAsync(uow, householdId, userId, row, ct);
        await PayAsync(uow, householdId, userId, row, item, dueDate, ct);
        if (!quiet)
        {
            await activity.RecordAsync(uow, householdId, userId, "transaction.matched", "transaction", row.Id,
                ActivitySummaries.TransactionConfirmedOccurrence(item.Name, row.Magnitude, currency),
                new
                {
                    transactionId = row.Id,
                    itemId = item.Id,
                    dueDate,
                    amountCents = row.Magnitude,
                    previousStatus = row.Status,
                    reversedExpense = reversal.HadExpense,
                    reversedPayment = reversal.HadPayment,
                    unpairedPeerId = reversal.PeerId,
                }, ct, actorName);
        }

        return null;
    }

    public async Task<InboxError?> IgnoreAsync(UnitOfWork uow, Guid householdId, Guid userId, string? actorName, string currency, ImportedTransactionRow row, bool quiet, CancellationToken ct, string reason = "manual")
    {
        if (!row.IsPending)
        {
            return new InboxError("status", "Only pending transactions can be ignored; restore it first.");
        }

        await imports.IgnoreAsync(uow.Connection, householdId, row.Id, userId, time.Now(), ct, reason);
        if (!quiet)
        {
            await activity.RecordAsync(uow, householdId, userId, "transaction.ignored", "transaction", row.Id,
                ActivitySummaries.TransactionIgnored(DisplayName(row), row.AmountCents, currency),
                new { transactionId = row.Id, amountCents = row.AmountCents, transfer = row.SuggestedTransfer, reason }, ct, actorName);
        }

        return null;
    }

    /// <summary>
    /// v5 "Mark as transfer": with <paramref name="peerId"/> both rows become <c>ignored / transfer</c> pointing at each other (peer: other
    /// account, opposite sign, |amount| within 1 %, ±5 days, not paired with a third row); without it the row is a one-sided transfer and its
    /// payee is learned (<c>payee_rules.is_transfer</c>). Any previous confirmation of either row is reversed first.
    /// </summary>
    public async Task<InboxError?> TransferAsync(UnitOfWork uow, Guid householdId, Guid userId, string? actorName, string currency, ImportedTransactionRow row, Guid? peerId, CancellationToken ct)
    {
        if (row.IsHidden)
        {
            return new InboxError("status", "This authorization was dropped or superseded; restore it first.");
        }

        ImportedTransactionRow? peer = null;
        if (peerId is { } pid)
        {
            if (pid == row.Id)
            {
                return new InboxError("peerId", "A transaction cannot be its own transfer peer.");
            }

            await imports.LockAsync(uow.Connection, householdId, pid, ct);
            peer = await imports.GetAsync(uow.Connection, householdId, pid, ct);
            if (peer is null)
            {
                return new InboxError("peerId", "Unknown transaction.");
            }

            if (peer.IsHidden)
            {
                return new InboxError("peerId", "That transaction was dropped or superseded.");
            }

            if (!TransactionMatcher.IsTransferPeer(ToMatch(row), ToMatch(peer)))
            {
                return new InboxError("peerId", "The other side must be on another account, opposite in sign, within 1 % of the amount and 5 days.");
            }

            if (peer.TransferPeerId is { } existing && existing != row.Id)
            {
                return new InboxError("peerId", "That transaction is already paired with another transfer.");
            }
        }

        if (row.IsTransfer && row.TransferPeerId == peerId)
        {
            return null; // already exactly this
        }

        var now = time.Now();
        var reversal = await ReverseAsync(uow, householdId, userId, row, ct);
        if (peer is not null)
        {
            peer = await imports.GetAsync(uow.Connection, householdId, peer.Id, ct) ?? peer;
            await ReverseAsync(uow, householdId, userId, peer, ct);
            await imports.MarkTransferAsync(uow.Connection, householdId, peer.Id, row.Id, auto: false, userId, now, ct);
        }

        await imports.MarkTransferAsync(uow.Connection, householdId, row.Id, peer?.Id, auto: false, userId, now, ct);
        if (peer is null)
        {
            var norm = TransactionMatcher.NormalisePayee(row.Payee, row.Description);
            if (norm.Length > 0)
            {
                await imports.SetTransferRuleAsync(uow.Connection, householdId, norm, now, ct);
            }
        }

        await activity.RecordAsync(uow, householdId, userId, "transfer.paired", "transfer", row.Id,
            peer is null
                ? ActivitySummaries.TransferOneSided(DisplayName(row), row.Magnitude, currency)
                : ActivitySummaries.TransferPaired(row.AmountCents < 0 ? row.AccountName : peer.AccountName, row.AmountCents < 0 ? peer.AccountName : row.AccountName, row.Magnitude, currency),
            new { transactionId = row.Id, peerId = peer?.Id, amountCents = row.Magnitude, oneSided = peer is null, reversedExpense = reversal.HadExpense, reversedPayment = reversal.HadPayment, previousPeerId = reversal.PeerId },
            ct, actorName);
        return null;
    }

    /// <summary>Back to pending (v3 restore). A confirmed expense is deleted, a payment un-marked, a transfer peer returned to pending too.</summary>
    public async Task<InboxError?> RestoreAsync(UnitOfWork uow, Guid householdId, Guid userId, string? actorName, string currency, ImportedTransactionRow row, CancellationToken ct)
    {
        if (row.IsPending)
        {
            return new InboxError("status", "This transaction is already pending.");
        }

        var reversal = await ReverseAsync(uow, householdId, userId, row, ct);
        await imports.RestoreAsync(uow.Connection, householdId, row.Id, ct);
        await imports.BlockAutoAsync(uow.Connection, householdId, row.Id, ct);
        await activity.RecordAsync(uow, householdId, userId, "transaction.restored", "transaction", row.Id,
            ActivitySummaries.TransactionRestored(DisplayName(row), row.AmountCents, currency),
            new { transactionId = row.Id, amountCents = row.AmountCents, hadExpense = reversal.HadExpense, hadPayment = reversal.HadPayment, peerId = reversal.PeerId }, ct, actorName);
        return null;
    }

    /// <summary>
    /// v5 "Unmatch / Not a transfer": works for every status. Reverses expense / payment / transfer (the peer goes back to pending too) and
    /// returns the row to pending. Already pending → no-op. A learned transfer payee rule is kept (the next explicit choice overrides it).
    /// </summary>
    public async Task<InboxError?> UnmatchAsync(UnitOfWork uow, Guid householdId, Guid userId, string? actorName, string currency, ImportedTransactionRow row, CancellationToken ct)
    {
        if (row.IsPending)
        {
            return null;
        }

        var reversal = await ReverseAsync(uow, householdId, userId, row, ct);
        await imports.RestoreAsync(uow.Connection, householdId, row.Id, ct);
        await imports.BlockAutoAsync(uow.Connection, householdId, row.Id, ct);
        var data = new { transactionId = row.Id, previousStatus = row.Status, ignoreReason = row.IgnoreReason, hadExpense = reversal.HadExpense, hadPayment = reversal.HadPayment, peerId = reversal.PeerId };
        if (reversal.WasTransfer)
        {
            await activity.RecordAsync(uow, householdId, userId, "transfer.unpaired", "transfer", row.Id,
                ActivitySummaries.TransferUnpaired(DisplayName(row), row.Magnitude, currency), data, ct, actorName);
        }
        else
        {
            await activity.RecordAsync(uow, householdId, userId, "transaction.unmatched", "transaction", row.Id,
                ActivitySummaries.TransactionUnmatched(DisplayName(row), row.AmountCents, currency), data, ct, actorName);
        }

        return null;
    }

    /// <summary>Un-marking an occurrence by hand puts the inbox row that paid it back to pending. Returns that row (null when none).</summary>
    public async Task<ImportedTransactionRow?> RestoreForOccurrenceAsync(NpgsqlConnection conn, Guid householdId, Guid itemId, DateOnly dueDate, CancellationToken ct)
    {
        var row = await imports.FindConfirmedForOccurrenceAsync(conn, householdId, itemId, dueDate, ct);
        if (row is null)
        {
            return null;
        }

        await imports.RestoreAsync(conn, householdId, row.Id, ct);
        await imports.BlockAutoAsync(conn, householdId, row.Id, ct);
        return row;
    }

    // ---------------------------------------------------------------------------------------------
    // v5: pending lifecycle (called by the sync, actor = null)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The bank changed a row's amount or date (tip, posted fuel, same id or replacement): a confirmed expense follows (amount + date), a
    /// matched occurrence payment follows (amount, so the variance updates).
    /// </summary>
    public async Task FollowPostedAsync(NpgsqlConnection conn, Guid householdId, ImportedTransactionRow row, long amountCents, DateOnly postedOn, CancellationToken ct)
    {
        if (row.Status != "confirmed")
        {
            return;
        }

        if (row.ConfirmedExpenseId is { } expenseId)
        {
            await expenses.SetAmountAsync(conn, householdId, expenseId, Math.Abs(amountCents), postedOn, time.Now(), ct);
        }

        if (row is { ConfirmedItemId: { } itemId, ConfirmedDueDate: { } dueDate })
        {
            await items.SetPaymentAmountAsync(conn, householdId, itemId, dueDate, Math.Abs(amountCents), ct);
        }
    }

    /// <summary>
    /// Hides a row as <c>dropped</c> (authorization fell off the feed) or <c>superseded</c> (its posted version is another row). Any
    /// confirmation is reversed (expense deleted / occurrence unpaid / transfer peer back to pending) and <c>transaction.dropped</c> is written.
    /// </summary>
    public async Task HideAsync(UnitOfWork uow, Guid householdId, Guid systemUserId, string currency, ImportedTransactionRow row, string reason, CancellationToken ct)
    {
        var reversal = await ReverseAsync(uow, householdId, null, row, ct, systemUserId);
        await imports.HideAsync(uow.Connection, householdId, row.Id, reason, time.Now(), ct);
        await activity.RecordAsync(uow.Connection, uow.Transaction, householdId, null, "transaction.dropped", "transaction", row.Id,
            reason == PendingReconciler.Dropped
                ? ActivitySummaries.TransactionDropped(DisplayName(row), row.AmountCents, currency)
                : ActivitySummaries.TransactionSuperseded(DisplayName(row), row.AmountCents, currency),
            new { transactionId = row.Id, amountCents = row.AmountCents, reason, previousStatus = row.Status, hadExpense = reversal.HadExpense, hadPayment = reversal.HadPayment, peerId = reversal.PeerId }, ct);
    }

    // ---------------------------------------------------------------------------------------------
    // v5: candidates
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Occurrences of active items within ±<paramref name="days"/> of the row, sign-compatible, ranked by
    /// <see cref="TransactionMatcher.CandidateScore"/>; plus transfer peers (other account, opposite sign, 1 %, ±5 days, unpaired).
    /// </summary>
    public async Task<CandidatesResponse> CandidatesAsync(NpgsqlConnection conn, Guid householdId, ImportedTransactionRow row, int days, CancellationToken ct)
    {
        var tx = ToMatch(row);
        var itemRows = await items.ListAsync(householdId, null, ct);
        var rulesByNorm = await RulesByNormAsync(conn, householdId, ct);
        var norm = TransactionMatcher.NormalisePayee(row.Payee, row.Description);
        rulesByNorm.TryGetValue(norm, out var rule);

        var list = await occurrences.ListAsync(householdId, itemRows, row.PostedOn.AddDays(-days), row.PostedOn.AddDays(days), ct);
        var candidates = list
            .Select(o =>
            {
                var paidByThis = row is { Status: "confirmed", ConfirmedItemId: { } ci, ConfirmedDueDate: { } cd } && ci == o.ItemId && cd == o.DueDate;
                var score = TransactionMatcher.CandidateScore(o.Kind, o.AmountCents, o.DueDate, tx, days, rule?.ItemId == o.ItemId);
                return (Occurrence: o, PaidByThis: paidByThis, Score: score ?? (paidByThis ? 0 : (int?)null));
            })
            .Where(c => c.Score is not null)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => Math.Abs(c.Occurrence.DueDate.DayNumber - row.PostedOn.DayNumber))
            .ThenBy(c => c.Occurrence.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .Select(c => new MatchCandidateDto(c.Occurrence.ItemId, c.Occurrence.Name, c.Occurrence.Kind, c.Occurrence.DueDate, c.Occurrence.AmountCents,
                c.Occurrence.Paid, c.PaidByThis, c.Score!.Value))
            .ToList();

        var window = TransactionMatcher.ManualTransferWindowDays;
        var peers = (await imports.ListVisibleInWindowAsync(conn, householdId, row.PostedOn.AddDays(-window), row.PostedOn.AddDays(window), ct))
            .Where(p => TransactionMatcher.IsTransferPeer(tx, ToMatch(p)) && (p.TransferPeerId is null || p.TransferPeerId == row.Id))
            .OrderBy(p => p.TransferPeerId == row.Id ? 0 : 1)
            .ThenBy(p => Math.Abs(p.PostedOn.DayNumber - row.PostedOn.DayNumber))
            .ThenBy(p => Math.Abs(p.Magnitude - row.Magnitude))
            .ThenBy(p => p.Id)
            .Take(MaxCandidates)
            .Select(p => p.ToDto())
            .ToList();

        return new CandidatesResponse(candidates, peers);
    }

    /// <summary>
    /// From the bill side: rows within ±<paramref name="days"/> of the due date that are still to review or confirmed as an expense
    /// (not already paying an occurrence, not transfers), sign-compatible, ranked by the same score.
    /// </summary>
    public async Task<IReadOnlyList<InboxItemDto>> TransactionCandidatesAsync(NpgsqlConnection conn, Guid householdId, RecurringItemRow item, DateOnly dueDate, int days, CancellationToken ct)
    {
        var rulesByNorm = await RulesByNormAsync(conn, householdId, ct);
        var rows = await imports.ListVisibleInWindowAsync(conn, householdId, dueDate.AddDays(-days), dueDate.AddDays(days), ct);
        return rows
            .Where(r => r.IsPending || r is { Status: "confirmed", ConfirmedExpenseId: not null })
            .Select(r =>
            {
                var ruleHit = rulesByNorm.TryGetValue(TransactionMatcher.NormalisePayee(r.Payee, r.Description), out var rule) && rule.ItemId == item.Id;
                return (Row: r, Score: TransactionMatcher.CandidateScore(item.Kind, item.AmountCents, dueDate, ToMatch(r), days, ruleHit));
            })
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => Math.Abs(x.Row.PostedOn.DayNumber - dueDate.DayNumber))
            .ThenBy(x => x.Row.Id)
            .Take(MaxCandidates)
            .Select(x => x.Row.ToDto())
            .ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Undoes what the row stands for without changing its own status: deletes the confirmed expense (reversing a deduction), un-marks the
    /// occurrence it paid (autopay skip for autopay items), and returns a transfer peer to pending. The caller then sets the row's new state.
    /// </summary>
    public async Task<Reversal> ReverseAsync(UnitOfWork uow, Guid householdId, Guid? actorId, ImportedTransactionRow row, CancellationToken ct, Guid? systemUserId = null)
    {
        var now = time.Now();
        var hadExpense = false;
        var hadPayment = false;
        Guid? peerId = null;

        if (row.Status == "confirmed" && row.ConfirmedExpenseId is { } expenseId)
        {
            var expense = await expenses.DeleteAsync(uow.Connection, householdId, expenseId, ct);
            hadExpense = expense is not null;
            if (expense is { Deducted: true, AccountId: { } accountId } && await accounts.GetAsync(uow.Connection, householdId, accountId, ct) is { } account)
            {
                await accounts.AdjustBalanceAsync(uow.Connection, householdId, accountId, actorId ?? systemUserId ?? expense.CreatedBy, ExpenseEndpoints.Delta(account.Type, expense.AmountCents), now, ct);
            }
        }

        if (row is { Status: "confirmed", ConfirmedItemId: { } itemId, ConfirmedDueDate: { } dueDate })
        {
            var removed = await items.DeletePaymentAsync(uow.Connection, itemId, dueDate, ct);
            hadPayment = removed is not null;
            if (removed is not null && await items.GetAsync(uow.Connection, householdId, itemId, ct) is { AutoPay: true })
            {
                // The scheduler would otherwise re-pay the occurrence on its next tick.
                await items.InsertAutopaySkipAsync(uow.Connection, itemId, dueDate, actorId, now, ct);
            }
        }

        if (row.IsTransfer && row.TransferPeerId is { } pid)
        {
            await imports.LockAsync(uow.Connection, householdId, pid, ct);
            if (await imports.GetAsync(uow.Connection, householdId, pid, ct) is { } peer && peer.TransferPeerId == row.Id)
            {
                await imports.RestoreAsync(uow.Connection, householdId, peer.Id, ct);
                peerId = peer.Id;
            }
        }

        return new Reversal(hadExpense, hadPayment, peerId, row.IsTransfer);
    }

    private async Task<(RecurringItemRow? Item, InboxError? Error)> CheckOccurrenceAsync(NpgsqlConnection conn, Guid householdId, ImportedTransactionRow row, Guid itemId, DateOnly dueDate, CancellationToken ct)
    {
        var item = await items.GetAsync(conn, householdId, itemId, ct);
        if (item is null)
        {
            return (null, new InboxError("itemId", "Unknown recurring item."));
        }

        if (!TransactionMatcher.SignMatchesKind(item.Kind, row.IsOutflow))
        {
            return (item, new InboxError("itemId", item.Kind.IsOutflow()
                ? "A bill or subscription payment must be money out."
                : "An income payment must be money in."));
        }

        if (!RecurrenceEngine.IsOccurrence(item.Rule, dueDate))
        {
            return (item, new InboxError("dueDate", "Not a due date of this item."));
        }

        return (item, null);
    }

    private async Task PayAsync(UnitOfWork uow, Guid householdId, Guid userId, ImportedTransactionRow row, RecurringItemRow item, DateOnly dueDate, CancellationToken ct)
    {
        var now = time.Now();
        await items.UpsertPaymentAsync(uow.Connection, item.Id, dueDate, userId, row.Magnitude, now, auto: false, PaymentSource.Bank, ct);
        await imports.ConfirmOccurrenceAsync(uow.Connection, householdId, row.Id, item.Id, dueDate, userId, auto: false, now, ct);
        await LearnFromMatchAsync(uow.Connection, householdId, row, item, now, ct);
    }

    /// <summary>
    /// A confirmed occurrence match (by a person or automatically) teaches two things: the payee rule payee → item (v3) and, v6, the
    /// transaction's normalised payee as a learned bank name of the item (deduped against both lists, ≤ 10, oldest learned evicted).
    /// </summary>
    public async Task LearnFromMatchAsync(NpgsqlConnection conn, Guid householdId, ImportedTransactionRow row, RecurringItemRow item, DateTime now, CancellationToken ct)
    {
        await LearnRuleAsync(conn, householdId, row, null, item.Id, now, ct);

        // Re-read: an earlier match in the same batch may already have taught this item a name.
        var current = await items.GetAsync(conn, householdId, item.Id, ct) ?? item;
        var learned = BankNames.Learn(current.BankNames, current.BankNamesLearned, BankNames.LearnedForm(row.Payee, row.Description));
        if (learned is not null)
        {
            await items.SetLearnedNamesAsync(conn, householdId, item.Id, learned, now, ct);
        }
    }

    private async Task<Dictionary<string, PayeeRule>> RulesByNormAsync(NpgsqlConnection conn, Guid householdId, CancellationToken ct) =>
        (await imports.ListRulesAsync(conn, householdId, ct)).Select(ToRule).GroupBy(r => r.PayeeNorm, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>Learns the rule and re-suggests pending rows with the same payee (a confirmed Kroger makes the other Kroger rows suggested).</summary>
    private async Task LearnRuleAsync(NpgsqlConnection conn, Guid householdId, ImportedTransactionRow row, Guid? categoryId, Guid? itemId, DateTime now, CancellationToken ct)
    {
        var norm = TransactionMatcher.NormalisePayee(row.Payee, row.Description);
        if (norm.Length == 0)
        {
            return;
        }

        await imports.UpsertRuleAsync(conn, householdId, norm, categoryId, itemId, now, ct);
        if (categoryId is not { } category)
        {
            return;
        }

        var rule = (await imports.ListRulesAsync(conn, householdId, ct)).FirstOrDefault(r => r.PayeeNorm == norm);
        var confidence = TransactionMatcher.RuleConfidence + Math.Clamp(rule?.Hits ?? 1, 0, TransactionMatcher.RuleMaxHits);
        var pending = await imports.ListPendingAsync(conn, householdId, ct);
        foreach (var other in pending.Where(p => p.Id != row.Id && p.IsOutflow && p.SuggestedItemId is null && !p.SuggestedTransfer && p.Confidence < confidence))
        {
            if (TransactionMatcher.NormalisePayee(other.Payee, other.Description) == norm)
            {
                await imports.SetSuggestionAsync(conn, householdId, other.Id, Suggestion.Category(category, confidence), ct);
            }
        }
    }

    public static MatchTransaction ToMatch(ImportedTransactionRow r) => new(r.Id, r.AccountId, r.PostedOn, r.AmountCents, r.Description, r.Payee);

    public static PayeeRule ToRule(PayeeRuleRow r) => new(r.PayeeNorm, r.CategoryId, r.ItemId, r.Hits, r.IsTransfer);

    public static Dictionary<string, Guid> CategoriesByName(IEnumerable<CategoryRow> rows)
    {
        var dict = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in rows)
        {
            dict.TryAdd(category.Name.Trim(), category.Id);
        }

        return dict;
    }

    /// <summary>The expense a confirmed row becomes is named <paramref name="name"/> when given, else payee ?? description (≤ 80 characters).</summary>
    public static string ExpenseName(ImportedTransactionRow row, string? name)
    {
        var expenseName = Truncate((string.IsNullOrWhiteSpace(name) ? row.Payee ?? row.Description : name).Trim(), MaxExpenseNameLength);
        return expenseName.Length == 0 ? "Bank transaction" : expenseName;
    }

    public static string DisplayName(ImportedTransactionRow row) => string.IsNullOrWhiteSpace(row.Payee) ? row.Description : row.Payee;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}
