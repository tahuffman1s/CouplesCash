using System.Collections.Concurrent;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Matching;
using Npgsql;

namespace CouplesCash.Api.Services;

/// <summary>Outcome of accept / dismiss. Exactly one of the members describes it.</summary>
public sealed record BillSuggestionOutcome(BillSuggestionAcceptedDto? Accepted, InboxError? Error, bool NotFound = false, string? Conflict = null);

/// <summary>
/// Found bills (PLAN-V6 §5): asks the matcher's <c>/v1/detect-recurring</c> for regular payments in the household's last 180 days of
/// imported transactions (no transfers, no dropped/superseded rows), never suggesting what an item already covers (knownNames), and
/// keeps <c>bill_suggestions</c> in step (dismissed fingerprints never return). There is no built-in detector: without the matcher,
/// detection is skipped. Accepting a suggestion creates the item with the detected bank name and matches the transactions it came from.
/// </summary>
public sealed class BillSuggestionService(
    NpgsqlDataSource db,
    ImportRepository imports,
    RecurringRepository items,
    BillSuggestionRepository suggestions,
    HouseholdRepository households,
    InboxService inbox,
    ActivityService activity,
    IMatcherClient matcher,
    MatcherStatusTracker status,
    MatchingQueue queue,
    TimeProvider time,
    ILogger<BillSuggestionService> logger)
{
    public const int LookbackDays = 180;

    /// <summary>Keeps the request well under the service's 1 MB / 5000-row limits.</summary>
    public const int MaxTransactions = 3000;

    private readonly ConcurrentDictionary<Guid, DateTime> _lastRun = new();

    /// <summary>Runs detection for one household. Returns false when it was skipped (matcher not configured or unavailable).</summary>
    public async Task<bool> DetectAsync(Guid householdId, CancellationToken ct)
    {
        var now = time.Now();
        _lastRun[householdId] = now;
        if (!matcher.Enabled)
        {
            return false;
        }

        if (await households.GetRowAsync(householdId, ct) is not { } household)
        {
            return false;
        }

        var today = DateOnly.FromDateTime(SchedulerRules.ToLocal(now, household.Timezone));
        IReadOnlyList<DetectionRow> rows;
        IReadOnlyList<string> knownNames;
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            rows = await imports.ListForDetectionAsync(conn, householdId, today.AddDays(-LookbackDays), MaxTransactions, ct);
            knownNames = (await items.ListAsync(conn, householdId, null, ct))
                .SelectMany(i => BankNames.AllNames(i.Name, i.BankNames, i.BankNamesLearned))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5000)
                .ToList();
        }

        if (rows.Count == 0)
        {
            return true;
        }

        MatcherDetectResponse response;
        try
        {
            response = await matcher.DetectRecurringAsync(new MatcherDetectRequest(
                today,
                rows.Select(r => new MatcherDetectTransaction(r.Id.ToString("D"), r.PostedOn, r.AmountCents, r.Description, r.Payee)).ToList(),
                knownNames), ct);
            status.RecordSuccess(null, now);
        }
        catch (MatcherException ex)
        {
            if (status.RecordFailure(ex.Failure, now))
            {
                logger.LogWarning("Matcher unavailable ({Failure}: {Message}); found bills for household {HouseholdId} skipped (no built-in detector)",
                    ex.Failure, ex.Message, householdId);
            }

            return false;
        }

        var byId = rows.ToDictionary(r => r.Id);
        var detected = response.Suggestions.Select(s => ToDetected(s, byId)).OfType<DetectedBill>().ToList();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var plan = BillSuggestionRules.Plan(await suggestions.ListStoredAsync(uow.Connection, householdId, ct), detected);
        foreach (var bill in plan.Upserts)
        {
            var lastSeen = bill.TransactionIds.Select(id => byId[id].PostedOn).DefaultIfEmpty().Max();
            await suggestions.UpsertOpenAsync(uow.Connection, householdId, bill, bill.TransactionIds.Count == 0 ? null : lastSeen, now, ct);
        }

        await suggestions.DeleteOpenAsync(uow.Connection, householdId, plan.RemoveOpen, ct);
        await uow.CommitAsync(ct);
        status.RecordFoundBills(now);
        logger.LogInformation("Scheduler found bills for household {HouseholdId}: {Count} open suggestion(s) from {Rows} transaction(s)", householdId, plan.Upserts.Count, rows.Count);
        return true;
    }

    /// <summary>Scheduler phase "found-bills": daily per household with recent imports (also queued after every sync / CSV import).</summary>
    public async Task DetectDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (!matcher.Enabled)
        {
            return;
        }

        var since = DateOnly.FromDateTime(nowUtc).AddDays(-LookbackDays);
        foreach (var householdId in await imports.HouseholdsWithImportsAsync(since, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!RematchRules.IsDue(_lastRun.TryGetValue(householdId, out var last) ? last : null, nowUtc, RematchRules.FoundBillsInterval))
            {
                continue;
            }

            try
            {
                await DetectAsync(householdId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduler found bills for household {HouseholdId} failed", householdId);
            }
        }
    }

    public async Task<IReadOnlyList<BillSuggestionDto>> ListAsync(Guid householdId, CancellationToken ct) =>
        (await suggestions.ListOpenAsync(householdId, ct)).Select(ToDto).ToList();

    /// <summary>
    /// Creates the item (detected values, overridden by <paramref name="request"/>), anchored so the sample transactions fall on its
    /// occurrences, with <c>bankNames = [bankName]</c>; matches every sample that is still to review or confirmed as an expense to its
    /// nearest occurrence; marks the suggestion accepted.
    /// </summary>
    public async Task<BillSuggestionOutcome> AcceptAsync(Guid householdId, Guid userId, string? actorName, Guid id, BillSuggestionAcceptRequest request, CancellationToken ct)
    {
        var currency = await households.CurrencyAsync(householdId, ct);
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var conn = uow.Connection;
        var suggestion = await suggestions.GetForUpdateAsync(conn, householdId, id, ct);
        if (suggestion is null)
        {
            return new BillSuggestionOutcome(null, null, NotFound: true);
        }

        if (suggestion.Status != BillSuggestionRules.Open)
        {
            return new BillSuggestionOutcome(null, null, Conflict: suggestion.Status == BillSuggestionRules.Accepted
                ? "This suggestion was already added."
                : "This suggestion was dismissed.");
        }

        var kind = request.Kind ?? suggestion.Kind;
        if (kind.IsOutflow() != suggestion.Kind.IsOutflow())
        {
            return new BillSuggestionOutcome(null, new InboxError("kind", suggestion.Kind.IsOutflow()
                ? "These payments are money out: use bill or subscription."
                : "These payments are money in: use income."));
        }

        var samples = (await imports.ListByIdsAsync(conn, householdId, suggestion.SampleTransactionIds, ct)).OrderBy(r => r.PostedOn).ThenBy(r => r.Id).ToList();
        var frequency = request.Frequency ?? suggestion.Frequency;
        var nextDue = request.NextDue ?? suggestion.NextDue;
        var anchor = BillSuggestionRules.AnchorFor(frequency, nextDue, samples.Count == 0 ? null : samples[0].PostedOn);
        var name = string.IsNullOrWhiteSpace(request.Name) ? suggestion.Name : request.Name.Trim();
        var categoryId = kind.IsOutflow()
            ? samples.Select(r => r.ConfirmedCategoryId ?? r.SuggestedCategoryId).OfType<Guid>().GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => (Guid?)g.Key).FirstOrDefault()
            : null;
        var accountId = samples.Select(r => r.AccountId).OfType<Guid>().GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => (Guid?)g.Key).FirstOrDefault();

        var now = time.Now();
        var bankNames = BankNames.FromRequest([suggestion.BankName], null);
        var fields = new RecurringFields(name, kind, request.AmountCents ?? suggestion.AmountCents, categoryId, accountId, frequency, anchor,
            EndDate: null, AutoPay: false, Notes: null, Url: null, BankNames: bankNames, AmountVaries: suggestion.AmountVaries);
        var item = await items.CreateAsync(conn, householdId, userId, fields, now, ct);
        await RecurringEndpoints.SyncBankNameRulesAsync(imports, conn, householdId, item.Id, [], item.BankNames, [], now, ct);

        var matched = 0;
        var window = BillSuggestionRules.MatchWindowDays(frequency);
        foreach (var sample in samples)
        {
            await imports.LockAsync(conn, householdId, sample.Id, ct);
            var row = await imports.GetAsync(conn, householdId, sample.Id, ct);
            if (row is null || row.IsHidden || !(row.IsPending || row is { Status: "confirmed", ConfirmedExpenseId: not null }))
            {
                continue;
            }

            if (BillSuggestionRules.NearestOccurrence(item.Rule, row.PostedOn, window) is not { } due)
            {
                continue;
            }

            if (await inbox.MatchAsync(uow, householdId, userId, actorName, currency, row, item.Id, due, quiet: true, ct) is null)
            {
                matched++;
            }
        }

        await suggestions.SetStatusAsync(conn, householdId, suggestion.Id, BillSuggestionRules.Accepted, item.Id, now, ct);
        await activity.RecordAsync(uow, householdId, userId, "recurring.created", "recurring", item.Id,
            ActivitySummaries.BillSuggestionAccepted(item, matched, currency),
            new { itemId = item.Id, kind = item.Kind.ToDb(), amountCents = item.AmountCents, suggestionId = suggestion.Id, matched, fromSuggestion = true }, ct, actorName);

        var fresh = await items.GetAsync(conn, householdId, item.Id, ct) ?? item;
        uow.AfterCommit(() => queue.Enqueue(householdId, MatchingJobKind.Rematch));
        await uow.CommitAsync(ct);
        return new BillSuggestionOutcome(new BillSuggestionAcceptedDto(fresh.ToDto(time.Today()), matched), null);
    }

    /// <summary>Dismisses an open suggestion ("Not a bill"); it is never suggested again. Dismissing twice is a no-op.</summary>
    public async Task<BillSuggestionOutcome> DismissAsync(Guid householdId, Guid userId, string? actorName, Guid id, CancellationToken ct)
    {
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var suggestion = await suggestions.GetForUpdateAsync(uow.Connection, householdId, id, ct);
        if (suggestion is null)
        {
            return new BillSuggestionOutcome(null, null, NotFound: true);
        }

        if (suggestion.Status == BillSuggestionRules.Accepted)
        {
            return new BillSuggestionOutcome(null, null, Conflict: "This suggestion was already added.");
        }

        if (suggestion.Status == BillSuggestionRules.Open)
        {
            await suggestions.SetStatusAsync(uow.Connection, householdId, suggestion.Id, BillSuggestionRules.Dismissed, null, time.Now(), ct);
            await activity.RecordAsync(uow, householdId, userId, "bill_suggestion.dismissed", "bill_suggestion", suggestion.Id,
                ActivitySummaries.BillSuggestionDismissed(suggestion.Name),
                new { suggestionId = suggestion.Id, name = suggestion.Name, bankName = suggestion.BankName, frequency = suggestion.Frequency.ToDb() }, ct, actorName);
        }

        await uow.CommitAsync(ct);
        return new BillSuggestionOutcome(null, null);
    }

    public static BillSuggestionDto ToDto(BillSuggestionRow r) =>
        new(r.Id, r.Name, r.Kind, r.AmountCents, r.AmountVaries, r.Frequency, r.NextDue, r.BankName, r.Score, r.SampleTransactionIds.Length,
            r.LastSeen ?? DateOnly.FromDateTime(r.UpdatedAt));

    /// <summary>A matcher suggestion in API terms, or null when it does not fit (unknown kind/frequency, no amount, no known transactions).</summary>
    internal static DetectedBill? ToDetected(MatcherRecurringSuggestion s, IReadOnlyDictionary<Guid, DetectionRow> rows)
    {
        if (BillSuggestionRules.ParseKind(s.Kind) is not { } kind || BillSuggestionRules.ParseFrequency(s.Frequency) is not { } frequency
            || string.IsNullOrWhiteSpace(s.BankName) || s.AmountCents == 0)
        {
            return null;
        }

        var ids = (s.TransactionIds ?? []).Select(t => Guid.TryParse(t, out var g) ? g : Guid.Empty).Where(rows.ContainsKey).Distinct().ToList();
        if (ids.Count == 0)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(s.Name) ? s.BankName.Trim() : s.Name.Trim();
        return new DetectedBill(Clip(name, 80), Clip(s.BankName.Trim(), BankNames.MaxLength), kind, frequency, Math.Abs(s.AmountCents), s.AmountVaries,
            s.NextDue, Math.Clamp(s.Score, 0, 100), ids);
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}
