using System.Collections.Concurrent;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Services;
using Npgsql;

namespace CouplesCash.Api.Matching;

/// <summary><see cref="MatchRunMode.NewRows"/>: rows a sync / CSV import just inserted. <see cref="MatchRunMode.Sweep"/>: every row still to review.</summary>
public enum MatchRunMode
{
    NewRows,
    Sweep,
}

/// <summary>What one matching run did. <see cref="Engine"/>: fuzzy (the matcher answered) | builtin (fallback or nothing to ask).</summary>
public sealed record MatchRunResult(string Engine, int Scored, int AutoMatched, int AutoCategorized, int AutoIgnored)
{
    public static readonly MatchRunResult Empty = new(MatchDetail.Builtin, 0, 0, 0, 0);
}

/// <summary>
/// v6 matching (PLAN-V6 §2): builds a <c>/v1/match</c> request from rows to review, the household's occurrences within ±10 days
/// (names = item name + bank names + learned names) and learned category rules; asks the Python matcher; on any failure (not
/// configured, 401/413/422/5xx, timeout, unreadable body) uses the built-in <see cref="TransactionMatcher"/> and logs a warning at most
/// once a minute (never descriptions). Then stores suggestion + score + detail, auto-ignores transfers (v5, new rows only), auto-applies
/// occurrence matches (level bills|all) and auto-categorises expenses (level all). Transfer rows never go to the matcher.
/// </summary>
public sealed class MatchingService(
    NpgsqlDataSource db,
    ImportRepository imports,
    BankRepository banks,
    RecurringRepository items,
    CategoryRepository categories,
    ExpenseRepository expenses,
    AccountRepository accounts,
    HouseholdRepository households,
    InboxService inbox,
    FridayService friday,
    ActivityService activity,
    IMatcherClient matcher,
    MatcherStatusTracker status,
    MatcherOptions options,
    TimeProvider time,
    ILogger<MatchingService> logger)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastSweep = new();

    private sealed record Decision(ImportedTransactionRow Row, Suggestion Suggestion, int? Score, MatchDetail? Detail, bool AutoOccurrence, bool AutoCategory);

    /// <summary>
    /// Scores <paramref name="rows"/> (only those still to review) inside the caller's unit of work and applies the automatic actions.
    /// <paramref name="systemUserId"/> is recorded as the creator of auto-categorised expenses (null = no auto-categorising).
    /// </summary>
    public async Task<MatchRunResult> RunAsync(UnitOfWork uow, Guid householdId, IReadOnlyList<ImportedTransactionRow> rows, MatchRunMode mode, Guid? systemUserId, CancellationToken ct)
    {
        var batch = rows.Where(r => r.IsPending && !r.IsHidden && r.AmountCents != 0).DistinctBy(r => r.Id).ToList();
        if (batch.Count == 0)
        {
            return MatchRunResult.Empty;
        }

        var conn = uow.Connection;
        if (await households.GetRowAsync(conn, householdId, ct) is not { } household)
        {
            return MatchRunResult.Empty;
        }

        var now = time.Now();
        var level = household.AutoMatchLevel;
        var today = DateOnly.FromDateTime(SchedulerRules.ToLocal(now, household.Timezone));
        var (from, to) = MatchingRules.CandidateRange(batch.Select(r => r.PostedOn));

        // v7: planned transfers are matched by FridayService (pairs / bills-account outflows), never by the matcher or as bills.
        var itemRows = (await items.ListAsync(conn, householdId, null, ct)).Where(i => i.Kind != Kind.Transfer).ToList();
        var itemsById = itemRows.ToDictionary(i => i.Id);
        // Only a bank payment closes an occurrence for matching: one ticked by hand or by autopay is still waiting for its bank row.
        var paid = (await items.ListPaymentsAsync(conn, householdId, from, to, ct))
            .Where(p => p.Source == PaymentSource.Bank).Select(p => (p.RecurringItemId, p.DueDate)).ToHashSet();
        var occurrences = itemRows
            .SelectMany(i => RecurrenceEngine.Expand(i.Rule, from, to).Select(d => (Item: i, Due: d, Paid: paid.Contains((i.Id, d)))))
            .ToList();
        var unpaid = occurrences.Where(o => !o.Paid)
            .Select(o => new MatchableOccurrence(o.Item.Id, o.Item.Name, o.Item.Kind, o.Item.AmountCents, o.Due, o.Item.Frequency == Frequency.Once, o.Item.AmountVaries))
            .ToList();

        var ruleRows = await imports.ListRulesAsync(conn, householdId, ct);
        var rules = ruleRows.Select(InboxService.ToRule).ToList();
        var rulesByNorm = rules.GroupBy(r => r.PayeeNorm, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var categoryRows = await categories.ListAsync(householdId, ct);
        var categoriesByName = InboxService.CategoriesByName(categoryRows);
        var categoryNames = categoryRows.ToDictionary(c => c.Id, c => c.Name);

        var batchIds = batch.Select(r => r.Id).ToHashSet();
        var otherPending = mode == MatchRunMode.NewRows
            ? (await imports.ListPendingInWindowAsync(conn, householdId, from, to, ct)).Where(r => !batchIds.Contains(r.Id)).ToList()
            : [];
        var mapped = new HashSet<Guid>(await banks.MappedAccountIdsAsync(conn, householdId, ct));
        foreach (var accountId in batch.Concat(otherPending).Select(r => r.AccountId).OfType<Guid>())
        {
            mapped.Add(accountId);
        }

        // Built-in pass first: it is the fallback, it finds transfers (pairs need the whole batch), and it fills category gaps.
        var txs = batch.Select(InboxService.ToMatch).ToList();
        var otherTxs = otherPending.Select(InboxService.ToMatch).ToList();
        var builtin = TransactionMatcher.SuggestAll(txs, unpaid, rules, categoriesByName, mapped, otherTxs);
        var pairs = TransactionMatcher.DetectTransferPairs(txs.Concat(otherTxs), mapped);
        var transferIds = batch
            .Where(r => builtin.TryGetValue(r.Id, out var s) && s is { Kind: SuggestionKind.Transfer, Confidence: >= TransactionMatcher.TransferConfidence })
            .Select(r => r.Id)
            .ToHashSet();
        var toSend = batch.Where(r => !transferIds.Contains(r.Id)).ToList();

        MatcherMatchResponse? fuzzy = null;
        if (toSend.Count > 0)
        {
            fuzzy = await TryMatchAsync(matcher, status, logger, householdId, BuildRequest(today, level, toSend, occurrences, ruleRows), now, ct);
        }

        var engine = fuzzy is null ? MatchDetail.Builtin : MatchDetail.Fuzzy;
        var fuzzyMatches = fuzzy?.Matches.GroupBy(m => m.TransactionId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var fuzzyCategories = fuzzy?.Categories.GroupBy(c => c.TransactionId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var decisions = new List<Decision>(batch.Count);
        foreach (var row in batch)
        {
            var tx = InboxService.ToMatch(row);
            if (transferIds.Contains(row.Id))
            {
                decisions.Add(new Decision(row, builtin[row.Id], null, null, false, false));
                continue;
            }

            if (fuzzyMatches is not null && fuzzyCategories is not null)
            {
                decisions.Add(FromFuzzy(row, tx, level, fuzzyMatches, fuzzyCategories, itemsById, categoryNames, rulesByNorm, categoriesByName));
                continue;
            }

            decisions.Add(FromBuiltin(row, tx, level, builtin.GetValueOrDefault(row.Id) ?? Suggestion.None, unpaid, itemsById, rulesByNorm, categoriesByName));
        }

        foreach (var d in decisions.Where(d => !Unchanged(d)))
        {
            await imports.SetMatchAsync(conn, householdId, d.Row.Id, d.Suggestion, d.Score, d.Detail is null ? null : MappingV3.SerializeDetail(d.Detail), ct);
        }

        var autoIgnored = 0;
        if (mode == MatchRunMode.NewRows)
        {
            // v5: an older pending row that pairs with a new one becomes a transfer too (unless it already has a stronger suggestion).
            var transferLegs = pairs.SelectMany(p => new[] { p.A, p.B }).ToHashSet();
            foreach (var other in otherPending.Where(o => transferLegs.Contains(o.Id) && !o.SuggestedTransfer && o.Confidence < TransactionMatcher.TransferConfidence))
            {
                await imports.SetSuggestionAsync(conn, householdId, other.Id, Suggestion.Transfer, ct);
            }

            // Pairs first (v5): only pairs that involve a new row count as "detected now".
            var suggested = new SuggestResult(decisions.ToDictionary(d => d.Row.Id, d => d.Suggestion), pairs.Where(p => batchIds.Contains(p.A) || batchIds.Contains(p.B)).ToList());
            autoIgnored = await inbox.AutoIgnoreTransfersAsync(uow, householdId, batch, suggested, ct);
        }

        var autoMatched = 0;
        foreach (var d in decisions.Where(d => d.AutoOccurrence).OrderByDescending(d => d.Score ?? 0).ThenBy(d => d.Row.PostedOn))
        {
            if (await AutoMatchAsync(uow, household, d, engine, now, ct))
            {
                autoMatched++;
            }
        }

        var autoCategorized = 0;
        if (systemUserId is { } systemUser)
        {
            foreach (var d in decisions.Where(d => d.AutoCategory))
            {
                if (await AutoCategorizeAsync(uow, household, systemUser, d, categoryNames, engine, now, ct))
                {
                    autoCategorized++;
                }
            }
        }

        status.RecordRun(householdId, new MatcherRun(now, engine, autoMatched + autoCategorized, batch.Count));
        return new MatchRunResult(engine, batch.Count, autoMatched, autoCategorized, autoIgnored);
    }

    /// <summary>
    /// Re-scores every To-review row of the household in its own transaction (scheduler sweep, immediate rematch). Runs for one household
    /// at a time (a second request waits for the first, then scores what changed meanwhile).
    /// </summary>
    public async Task<MatchRunResult> RematchAsync(Guid householdId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(householdId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            _lastSweep[householdId] = time.Now();
            await using var uow = await db.BeginUnitOfWorkAsync(ct);

            // v7: a new planned transfer / role may claim rows before they are scored (the scheduler's friday phase does the same).
            var claimed = await friday.RunAsync(uow, householdId, ct);
            var rows = await imports.ListPendingAsync(uow.Connection, householdId, ct);
            if (rows.Count == 0)
            {
                if (claimed.Total > 0)
                {
                    await uow.CommitAsync(ct);
                }

                return MatchRunResult.Empty;
            }

            var systemUser = await households.FirstMemberAsync(uow.Connection, householdId, ct);
            var result = await RunAsync(uow, householdId, rows, MatchRunMode.Sweep, systemUser, ct);
            await uow.CommitAsync(ct);
            if (result.AutoMatched + result.AutoCategorized > 0)
            {
                logger.LogInformation("Scheduler rematch of household {HouseholdId}: {Scored} scored ({Engine}), {Auto} auto-matched, {Categorized} auto-categorized",
                    householdId, result.Scored, result.Engine, result.AutoMatched, result.AutoCategorized);
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Scheduler phase "rematch": every household with rows to review, at most every <c>Matcher__RematchMinutes</c>.</summary>
    public async Task SweepDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        foreach (var householdId in await imports.HouseholdsWithPendingAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!RematchRules.IsDue(_lastSweep.TryGetValue(householdId, out var last) ? last : null, nowUtc, options.RematchInterval))
            {
                continue;
            }

            try
            {
                await RematchAsync(householdId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduler rematch of household {HouseholdId} failed", householdId);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Request / response
    // ---------------------------------------------------------------------------------------------

    /// <summary>A sweep re-scores every row to review; rows whose suggestion, score and detail did not change are not rewritten.</summary>
    private static bool Unchanged(Decision d)
    {
        var row = d.Row;
        var s = d.Suggestion;
        var detail = d.Detail is null ? null : new Contracts.MatchDetailDto(d.Detail.Name, d.Detail.Amount, d.Detail.Date, d.Detail.Reason, d.Detail.Engine);
        return row.SuggestedCategoryId == s.CategoryId
            && row.SuggestedItemId == s.ItemId
            && row.SuggestedDueDate == s.DueDate
            && row.SuggestedTransfer == (s.Kind == SuggestionKind.Transfer)
            && row.Confidence == Math.Clamp(s.Confidence, 0, 100)
            && row.MatchScore == d.Score
            && Equals(MappingV3.ParseDetail(row.MatchDetail), detail);
    }

    internal static MatcherMatchRequest BuildRequest(
        DateOnly today,
        AutoMatchLevel level,
        IReadOnlyList<ImportedTransactionRow> rows,
        IReadOnlyList<(RecurringItemRow Item, DateOnly Due, bool Paid)> occurrences,
        IReadOnlyList<PayeeRuleRow> rules) =>
        new(
            today,
            new MatcherMatchOptions(level.ToDb(), MatchingRules.AutoThreshold, MatchingRules.SuggestThreshold),
            rows.Select(r => new MatcherTransaction(r.Id.ToString("D"), r.AccountId?.ToString("D"), r.PostedOn, r.AmountCents, r.Description, r.Payee, r.Memo, r.Pending)).ToList(),
            occurrences.Select(o => new MatcherCandidate(
                o.Item.Id.ToString("D"),
                o.Due,
                o.Item.Kind.ToDb(),
                o.Item.Frequency.ToDb(),
                Math.Abs(o.Item.AmountCents),
                o.Item.AmountVaries,
                BankNames.AllNames(o.Item.Name, o.Item.BankNames, o.Item.BankNamesLearned),
                o.Item.AccountId?.ToString("D"),
                o.Paid)).ToList(),
            rules.Where(r => r is { CategoryId: not null, IsTransfer: false } && r.PayeeNorm.Length > 0)
                .Select(r => new MatcherCategoryRule(r.PayeeNorm, r.CategoryId!.Value.ToString("D"), Math.Max(0, r.Hits)))
                .ToList());

    /// <summary>
    /// The matcher's answer, or null when it is not configured or the call failed in any way (the caller then uses the built-in engine).
    /// Failures are recorded for <c>/matcher/status</c>; the warning is logged at most once a minute and names only the failure kind.
    /// </summary>
    internal static async Task<MatcherMatchResponse?> TryMatchAsync(
        IMatcherClient matcher,
        MatcherStatusTracker status,
        ILogger logger,
        Guid householdId,
        MatcherMatchRequest request,
        DateTime now,
        CancellationToken ct)
    {
        try
        {
            var response = await matcher.MatchAsync(request, ct);
            status.RecordSuccess(response.Version, now);
            return response;
        }
        catch (MatcherException ex)
        {
            if (status.RecordFailure(ex.Failure, now))
            {
                logger.LogWarning("Matcher unavailable ({Failure}: {Message}); household {HouseholdId} is matched by the built-in engine",
                    ex.Failure, ex.Message, householdId);
            }

            return null;
        }
    }

    private static Decision FromFuzzy(
        ImportedTransactionRow row,
        MatchTransaction tx,
        AutoMatchLevel level,
        IReadOnlyDictionary<string, MatcherMatch> matches,
        IReadOnlyDictionary<string, MatcherCategoryMatch> categoryMatches,
        IReadOnlyDictionary<Guid, RecurringItemRow> itemsById,
        IReadOnlyDictionary<Guid, string> categoryNames,
        IReadOnlyDictionary<string, PayeeRule> rulesByNorm,
        IReadOnlyDictionary<string, Guid> categoriesByName)
    {
        var key = row.Id.ToString("D");
        if (matches.TryGetValue(key, out var m)
            && Guid.TryParse(m.ItemId, out var itemId)
            && itemsById.TryGetValue(itemId, out var item)
            && TransactionMatcher.SignMatchesKind(item.Kind, row.IsOutflow)
            && RecurrenceEngine.IsOccurrence(item.Rule, m.DueDate))
        {
            var score = Math.Clamp(m.Score, 0, 100);
            var detail = new MatchDetail(m.NameScore, m.AmountScore, m.DateScore, m.Reason ?? "", MatchDetail.Fuzzy);
            var auto = m.Auto && MatchingRules.AutoAppliesOccurrences(level) && !row.AutoBlocked;
            return new Decision(row, Suggestion.Occurrence(item.Id, m.DueDate, score), score, detail, auto, false);
        }

        if (categoryMatches.TryGetValue(key, out var c) && Guid.TryParse(c.CategoryId, out var categoryId) && categoryNames.ContainsKey(categoryId))
        {
            var score = Math.Clamp(c.Score, 0, 100);
            var detail = new MatchDetail(score, 0, 0, c.Reason ?? "", MatchDetail.Fuzzy);
            var auto = c.Auto && MatchingRules.AutoAppliesCategories(level) && row.IsOutflow && !row.AutoBlocked;
            return new Decision(row, Suggestion.Category(categoryId, score), score, detail, false, auto);
        }

        // Nothing from the matcher: keep the built-in transfer keyword / learned / bank feed / keyword category suggestion (only a
        // very sure bank-feed category is applied automatically).
        var fallback = TransactionMatcher.SuggestWithoutOccurrence(tx, rulesByNorm, categoriesByName);
        return fallback.Kind == SuggestionKind.Category
            ? BuiltinCategoryDecision(row, tx, level, fallback, rulesByNorm, categoriesByName)
            : new Decision(row, fallback, null, null, false, false);
    }

    /// <summary>
    /// A built-in category suggestion with its reason; applied automatically (level all, money out) only when it is the bank feed's own
    /// category at VERY_HIGH confidence and no learned rule says otherwise.
    /// </summary>
    private static Decision BuiltinCategoryDecision(
        ImportedTransactionRow row,
        MatchTransaction tx,
        AutoMatchLevel level,
        Suggestion suggestion,
        IReadOnlyDictionary<string, PayeeRule> rulesByNorm,
        IReadOnlyDictionary<string, Guid> categoriesByName)
    {
        var rule = TransactionMatcher.FindRule(rulesByNorm, tx.Payee, tx.Description);
        var provider = ProviderCategories.Suggest(tx.ProviderCategory, tx.ProviderConfidence, tx.IsOutflow, categoriesByName);
        var fromProvider = rule?.CategoryId is null && provider is not null && provider.CategoryId == suggestion.CategoryId;
        var detail = fromProvider
            ? new MatchDetail(provider!.Confidence, 0, 0, provider.Reason, MatchDetail.Builtin)
            : MatchingRules.BuiltinCategoryDetail(suggestion, rule);
        var auto = fromProvider && provider!.Confidence >= ProviderCategories.AutoConfidence
            && MatchingRules.AutoAppliesCategories(level) && row.IsOutflow && !row.AutoBlocked;
        return new Decision(row, suggestion, suggestion.Confidence, detail, false, auto);
    }

    private static Decision FromBuiltin(
        ImportedTransactionRow row,
        MatchTransaction tx,
        AutoMatchLevel level,
        Suggestion suggestion,
        IReadOnlyList<MatchableOccurrence> unpaid,
        IReadOnlyDictionary<Guid, RecurringItemRow> itemsById,
        IReadOnlyDictionary<string, PayeeRule> rulesByNorm,
        IReadOnlyDictionary<string, Guid> categoriesByName)
    {
        switch (suggestion.Kind)
        {
            case SuggestionKind.Occurrence when suggestion is { ItemId: { } itemId, DueDate: { } due } && itemsById.TryGetValue(itemId, out var item):
                {
                    // a learned payee rule, or one of the bill's names whole in the transaction (any provider's wording)
                    var names = BankNames.AllNames(item.Name, item.BankNames, item.BankNamesLearned);
                    var ruleHit = TransactionMatcher.RuleMatchesItem(tx, itemId, rulesByNorm) || MatchingRules.NameContained(tx.Payee, tx.Description, names);
                    var occurrence = unpaid.FirstOrDefault(o => o.ItemId == itemId && o.DueDate == due)
                        ?? new MatchableOccurrence(itemId, item.Name, item.Kind, item.AmountCents, due, item.Frequency == Frequency.Once, item.AmountVaries);
                    var detail = MatchingRules.BuiltinOccurrenceDetail(tx, occurrence, names, ruleHit);
                    var auto = MatchingRules.BuiltinAuto(level, suggestion, ruleHit) && !row.AutoBlocked;
                    return new Decision(row, suggestion, suggestion.Confidence, detail, auto, false);
                }

            case SuggestionKind.Category:
                return BuiltinCategoryDecision(row, tx, level, suggestion, rulesByNorm, categoriesByName);

            default:
                return new Decision(row, suggestion, null, null, false, false);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Automatic actions
    // ---------------------------------------------------------------------------------------------

    /// <summary>Marks the occurrence paid with the bank amount (<c>paid_by NULL, auto true, source bank</c>) and confirms the row as auto.</summary>
    private async Task<bool> AutoMatchAsync(UnitOfWork uow, HouseholdRow household, Decision d, string engine, DateTime now, CancellationToken ct)
    {
        var conn = uow.Connection;
        await imports.LockAsync(conn, household.Id, d.Row.Id, ct);
        var row = await imports.GetAsync(conn, household.Id, d.Row.Id, ct);
        if (row is not { IsPending: true, AutoBlocked: false } || d.Suggestion is not { ItemId: { } itemId, DueDate: { } dueDate })
        {
            return false;
        }

        var item = await items.GetAsync(conn, household.Id, itemId, ct);
        if (item is null || !TransactionMatcher.SignMatchesKind(item.Kind, row.IsOutflow) || !RecurrenceEngine.IsOccurrence(item.Rule, dueDate))
        {
            return false;
        }

        if (await items.PayFromBankAsync(conn, item.Id, dueDate, row.Magnitude, now, ct) is null)
        {
            return false;
        }

        await imports.ConfirmOccurrenceAsync(conn, household.Id, row.Id, item.Id, dueDate, reviewer: null, auto: true, now, ct);
        await inbox.LearnFromMatchAsync(conn, household.Id, row, item, now, ct);
        await activity.RecordAsync(conn, uow.Transaction, household.Id, null, "transaction.automatched", "transaction", row.Id,
            ActivitySummaries.TransactionAutomatched(item.Name, row.Magnitude, household.Currency),
            new { transactionId = row.Id, itemId = item.Id, dueDate, amountCents = row.Magnitude, auto = true, reason = d.Detail?.Reason, score = d.Score, engine }, ct);
        return true;
    }

    /// <summary>Confirms an outflow as an expense in the suggested category (<c>deduct = false</c>, reviewer null, auto).</summary>
    private async Task<bool> AutoCategorizeAsync(UnitOfWork uow, HouseholdRow household, Guid systemUserId, Decision d, IReadOnlyDictionary<Guid, string> categoryNames, string engine, DateTime now, CancellationToken ct)
    {
        var conn = uow.Connection;
        await imports.LockAsync(conn, household.Id, d.Row.Id, ct);
        var row = await imports.GetAsync(conn, household.Id, d.Row.Id, ct);
        if (row is not { IsPending: true, AutoBlocked: false, IsOutflow: true } || d.Suggestion.CategoryId is not { } categoryId || !categoryNames.TryGetValue(categoryId, out var categoryName))
        {
            return false;
        }

        Guid? accountId = row.AccountId is { } a && await accounts.ExistsAsync(conn, household.Id, a, ct) ? a : null;
        var name = InboxService.ExpenseName(row, null);
        var expense = await expenses.CreateAsync(conn, household.Id, systemUserId,
            new ExpenseFields(name, row.Magnitude, categoryId, accountId, row.PostedOn, row.Memo, Deducted: false), now, ct);
        await imports.ConfirmExpenseAsync(conn, household.Id, row.Id, expense.Id, reviewer: null, now, ct, auto: true);
        await activity.RecordAsync(conn, uow.Transaction, household.Id, null, "transaction.autocategorized", "transaction", row.Id,
            ActivitySummaries.TransactionAutocategorized(name, categoryName, row.Magnitude, household.Currency),
            new { transactionId = row.Id, expenseId = expense.Id, categoryId, amountCents = row.Magnitude, auto = true, reason = d.Detail?.Reason, score = d.Score, engine }, ct);
        return true;
    }
}
