using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using Npgsql;

namespace CouplesCash.Api.Services;

/// <summary>A field-level reason a review action was refused (becomes a 400 <c>errors: { field: [message] }</c>).</summary>
public sealed record InboxError(string Field, string Message);

/// <summary>
/// The review inbox: runs the <see cref="TransactionMatcher"/> on new rows, auto-matches high-confidence recurring hits,
/// and applies the confirm / ignore / restore semantics of PLAN-V3 inside the caller's <see cref="UnitOfWork"/>.
/// </summary>
public sealed class InboxService(
    ImportRepository imports,
    BankRepository banks,
    RecurringRepository items,
    OccurrenceService occurrences,
    CategoryRepository categories,
    ExpenseRepository expenses,
    AccountRepository accounts,
    ActivityService activity,
    TimeProvider time)
{
    public const int MaxExpenseNameLength = 80;

    // ---------------------------------------------------------------------------------------------
    // Suggestions
    // ---------------------------------------------------------------------------------------------

    /// <summary>Computes and stores suggestions for freshly inserted rows; also flags already-pending rows that turn out to be the other leg of a transfer.</summary>
    public async Task<IReadOnlyDictionary<Guid, Suggestion>> SuggestAsync(NpgsqlConnection conn, Guid householdId, IReadOnlyList<ImportedTransactionRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return new Dictionary<Guid, Suggestion>();
        }

        // The window is the wider one-off window; FindOccurrence applies the per-item window (±5 recurring, ±7 once).
        var from = rows.Min(r => r.PostedOn).AddDays(-TransactionMatcher.OnceWindowDays);
        var to = rows.Max(r => r.PostedOn).AddDays(TransactionMatcher.OnceWindowDays);

        var itemRows = await items.ListAsync(householdId, null, ct);
        var onceItems = itemRows.Where(i => i.Frequency == Frequency.Once).Select(i => i.Id).ToHashSet();
        var unpaid = (await occurrences.ListAsync(householdId, itemRows, from, to, ct))
            .Where(o => o.Paid is null)
            .Select(o => new MatchableOccurrence(o.ItemId, o.Name, o.Kind, o.AmountCents, o.DueDate, onceItems.Contains(o.ItemId)))
            .ToList();
        var rules = (await imports.ListRulesAsync(conn, householdId, ct)).Select(ToRule).ToList();
        var categoriesByName = CategoriesByName(await categories.ListAsync(householdId, ct));

        var newIds = rows.Select(r => r.Id).ToHashSet();
        var otherPending = (await imports.ListPendingInWindowAsync(conn, householdId, from, to, ct)).Where(r => !newIds.Contains(r.Id)).ToList();
        var mapped = new HashSet<Guid>(await banks.MappedAccountIdsAsync(conn, householdId, ct));
        foreach (var accountId in rows.Concat(otherPending).Select(r => r.AccountId).OfType<Guid>())
        {
            mapped.Add(accountId);
        }

        var matches = rows.Select(ToMatch).ToList();
        var otherMatches = otherPending.Select(ToMatch).ToList();
        var suggestions = TransactionMatcher.SuggestAll(matches, unpaid, rules, categoriesByName, mapped, otherMatches);

        foreach (var (id, suggestion) in suggestions)
        {
            await imports.SetSuggestionAsync(conn, householdId, id, suggestion, ct);
        }

        // An older pending row that pairs with a new one becomes a transfer too (unless it already has a stronger suggestion).
        var transfers = TransactionMatcher.DetectTransfers(matches.Concat(otherMatches), mapped);
        foreach (var other in otherPending.Where(o => transfers.Contains(o.Id) && !o.SuggestedTransfer && o.Confidence < TransactionMatcher.TransferConfidence))
        {
            await imports.SetSuggestionAsync(conn, householdId, other.Id, Suggestion.Transfer, ct);
        }

        return suggestions;
    }

    /// <summary>
    /// Scheduler auto-mark: a recurring match with confidence ≥ 90 whose item has a learned payee rule for this payee is paid
    /// without a human (<c>paid_by NULL, auto true</c>); the row is confirmed with <c>auto</c>. Returns how many were matched.
    /// </summary>
    public async Task<int> AutoMatchAsync(UnitOfWork uow, Guid householdId, string currency, IReadOnlyList<ImportedTransactionRow> rows, IReadOnlyDictionary<Guid, Suggestion> suggestions, CancellationToken ct)
    {
        var candidates = rows
            .Where(r => suggestions.TryGetValue(r.Id, out var s) && s.Kind == SuggestionKind.Occurrence && s.Confidence >= TransactionMatcher.AutoMatchConfidence)
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var rulesByNorm = (await imports.ListRulesAsync(uow.Connection, householdId, ct)).Select(ToRule).ToDictionary(r => r.PayeeNorm, StringComparer.Ordinal);
        var now = time.Now();
        var matched = 0;

        foreach (var row in candidates)
        {
            var suggestion = suggestions[row.Id];
            var itemId = suggestion.ItemId!.Value;
            var dueDate = suggestion.DueDate!.Value;
            if (!TransactionMatcher.RuleMatchesItem(ToMatch(row), itemId, rulesByNorm))
            {
                continue;
            }

            var item = await items.GetAsync(uow.Connection, householdId, itemId, ct);
            if (item is null || !TransactionMatcher.SignMatchesKind(item.Kind, row.IsOutflow))
            {
                continue;
            }

            var payment = await items.InsertPaymentIfUnpaidAsync(uow.Connection, item.Id, dueDate, row.Magnitude, now, ct);
            if (payment is null)
            {
                continue;
            }

            await imports.ConfirmOccurrenceAsync(uow.Connection, householdId, row.Id, item.Id, dueDate, reviewer: null, auto: true, now, ct);
            await activity.RecordAsync(uow, householdId, null, "transaction.automatched", "transaction", row.Id,
                ActivitySummaries.TransactionAutomatched(item.Name, row.Magnitude, currency),
                new { transactionId = row.Id, itemId = item.Id, dueDate, amountCents = row.Magnitude, auto = true }, ct);
            matched++;
        }

        return matched;
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
        var expenseName = Truncate((string.IsNullOrWhiteSpace(name) ? row.Payee ?? row.Description : name).Trim(), MaxExpenseNameLength);
        if (expenseName.Length == 0)
        {
            expenseName = "Bank transaction";
        }

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

        var item = await items.GetAsync(uow.Connection, householdId, itemId, ct);
        if (item is null)
        {
            return new InboxError("itemId", "Unknown recurring item.");
        }

        if (!TransactionMatcher.SignMatchesKind(item.Kind, row.IsOutflow))
        {
            return new InboxError("itemId", item.Kind.IsOutflow()
                ? "A bill or subscription payment must be money out."
                : "An income payment must be money in.");
        }

        if (!RecurrenceEngine.IsOccurrence(item.Rule, dueDate))
        {
            return new InboxError("dueDate", "Not a due date of this item.");
        }

        if (await items.GetPaymentAsync(uow.Connection, item.Id, dueDate, ct) is not null)
        {
            return new InboxError("dueDate", "This occurrence is already paid.");
        }

        var now = time.Now();
        await items.UpsertPaymentAsync(uow.Connection, item.Id, dueDate, userId, row.Magnitude, now, auto: false, PaymentSource.Bank, ct);
        await imports.ConfirmOccurrenceAsync(uow.Connection, householdId, row.Id, item.Id, dueDate, userId, auto: false, now, ct);
        await LearnRuleAsync(uow.Connection, householdId, row, null, item.Id, now, ct);

        if (!quiet)
        {
            await activity.RecordAsync(uow, householdId, userId, "transaction.confirmed", "transaction", row.Id,
                ActivitySummaries.TransactionConfirmedOccurrence(item.Name, row.Magnitude, currency),
                new { transactionId = row.Id, itemId = item.Id, dueDate, amountCents = row.Magnitude, @as = "occurrence" }, ct, actorName);
        }

        return null;
    }

    public async Task<InboxError?> IgnoreAsync(UnitOfWork uow, Guid householdId, Guid userId, string? actorName, string currency, ImportedTransactionRow row, bool quiet, CancellationToken ct)
    {
        if (!row.IsPending)
        {
            return new InboxError("status", "Only pending transactions can be ignored; restore it first.");
        }

        await imports.IgnoreAsync(uow.Connection, householdId, row.Id, userId, time.Now(), ct);
        if (!quiet)
        {
            await activity.RecordAsync(uow, householdId, userId, "transaction.ignored", "transaction", row.Id,
                ActivitySummaries.TransactionIgnored(DisplayName(row), row.AmountCents, currency),
                new { transactionId = row.Id, amountCents = row.AmountCents, transfer = row.SuggestedTransfer }, ct, actorName);
        }

        return null;
    }

    /// <summary>Back to pending. A confirmed expense is deleted (deduction reversed if any); a confirmed payment is un-marked.</summary>
    public async Task<InboxError?> RestoreAsync(UnitOfWork uow, Guid householdId, Guid userId, string? actorName, string currency, ImportedTransactionRow row, CancellationToken ct)
    {
        if (row.IsPending)
        {
            return new InboxError("status", "This transaction is already pending.");
        }

        var now = time.Now();
        if (row.ConfirmedExpenseId is { } expenseId)
        {
            var expense = await expenses.DeleteAsync(uow.Connection, householdId, expenseId, ct);
            if (expense is { Deducted: true, AccountId: { } accountId } && await accounts.GetAsync(uow.Connection, householdId, accountId, ct) is { } account)
            {
                await accounts.AdjustBalanceAsync(uow.Connection, householdId, accountId, userId, ExpenseEndpoints.Delta(account.Type, expense.AmountCents), now, ct);
            }
        }

        if (row is { ConfirmedItemId: { } itemId, ConfirmedDueDate: { } dueDate })
        {
            var removed = await items.DeletePaymentAsync(uow.Connection, itemId, dueDate, ct);
            if (removed is not null && await items.GetAsync(uow.Connection, householdId, itemId, ct) is { AutoPay: true })
            {
                // The scheduler would otherwise re-pay the occurrence on its next tick.
                await items.InsertAutopaySkipAsync(uow.Connection, itemId, dueDate, userId, now, ct);
            }
        }

        await imports.RestoreAsync(uow.Connection, householdId, row.Id, ct);
        await activity.RecordAsync(uow, householdId, userId, "transaction.restored", "transaction", row.Id,
            ActivitySummaries.TransactionRestored(DisplayName(row), row.AmountCents, currency),
            new { transactionId = row.Id, amountCents = row.AmountCents, hadExpense = row.ConfirmedExpenseId is not null, hadPayment = row.ConfirmedItemId is not null }, ct, actorName);
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
        return row;
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

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

    public static PayeeRule ToRule(PayeeRuleRow r) => new(r.PayeeNorm, r.CategoryId, r.ItemId, r.Hits);

    public static Dictionary<string, Guid> CategoriesByName(IEnumerable<CategoryRow> rows)
    {
        var dict = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in rows)
        {
            dict.TryAdd(category.Name.Trim(), category.Id);
        }

        return dict;
    }

    public static string DisplayName(ImportedTransactionRow row) => string.IsNullOrWhiteSpace(row.Payee) ? row.Description : row.Payee;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}
