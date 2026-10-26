using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Matching;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace CouplesCash.Api.Endpoints;

public static class RecurringEndpoints
{
    public static RouteGroupBuilder MapRecurring(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/recurring").WithTags("Recurring").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Recurring items, optionally filtered by kind");
        group.MapPost("", Create).WithValidation<RecurringRequest>()
            .WithSummary("Create a bill, subscription or income (frequency 'once' = a planned one-off; bankNames ≤ 10 — legacy payeeHint maps to one — and amountVaries feed the matcher)");
        group.MapGet("/{id:guid}", Get).WithSummary("Get a recurring item");
        group.MapPut("/{id:guid}", Update).WithValidation<RecurringRequest>().WithSummary("Replace a recurring item");
        group.MapDelete("/{id:guid}", Archive).WithSummary("Archive (soft delete) a recurring item");
        group.MapPut("/{id:guid}/occurrences/{date}/paid", MarkPaid).WithValidation<MarkPaidRequest>()
            .WithSummary("Mark an occurrence paid (404 unless {date} is a real occurrence)");
        group.MapDelete("/{id:guid}/occurrences/{date}/paid", MarkUnpaid)
            .WithSummary("Un-mark an occurrence (undo for autopay: the scheduler will not re-pay it)");

        // v5: bills from transactions, payment history, linking a transaction from the occurrence side.
        group.MapPost("/from-transaction", FromTransaction).WithValidation<FromTransactionRequest>()
            .WithSummary("Create a bill/subscription/income from a bank transaction (anchor = its date, amount = |amount|, payeeHint = payee) and match it to the first occurrence");
        group.MapGet("/{id:guid}/payments", Payments)
            .WithSummary("Bill payments: the next occurrence and the last ?limit (default 12) with the paying transaction and variance, newest first");
        group.MapGet("/{id:guid}/occurrences/{date}/candidates", OccurrenceCandidates)
            .WithSummary("Unmatched bank rows within ±?days (default 10) of the due date, ranked (to review, or confirmed as an expense)");
        group.MapPost("/{id:guid}/occurrences/{date}/link", LinkTransaction).WithValidation<LinkTransactionRequest>()
            .WithSummary("Pay the occurrence with a bank transaction ({ transactionId }) · 409 when it is already paid by another transaction or by hand");

        // v6
        group.MapDelete("/{id:guid}/bank-names/learned/{index:int}", ForgetLearnedName)
            .WithSummary("Forget a learned bank name (0-based index into bankNamesLearned) · 404 when out of range");

        return group;
    }

    private static async Task<Results<Ok<List<RecurringItemDto>>, ValidationProblem>> List(
        ClaimsPrincipal principal,
        RecurringRepository items,
        TimeProvider time,
        CancellationToken ct,
        string? kind = null)
    {
        // Query enums arrive camelCase ("subscription"); parse case-insensitively instead of relying on default binding.
        Kind? filter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<Kind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Problems.Validation("kind", "Must be one of: bill, subscription, income.");
            }

            filter = parsed;
        }

        var rows = await items.ListAsync(principal.GetHouseholdId(), filter, ct);
        var today = time.Today();
        return TypedResults.Ok(rows.Select(i => i.ToDto(today)).ToList());
    }

    private static async Task<Results<Created<RecurringItemDto>, ValidationProblem>> Create(
        RecurringRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        ImportRepository imports,
        CategoryRepository categories,
        AccountRepository accounts,
        HouseholdRepository households,
        ActivityService activity,
        MatchingQueue matching,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        if (await CheckReferencesAsync(householdId, request, categories, accounts, ct) is { } problem)
        {
            return problem;
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var now = time.Now();
        var row = await items.CreateAsync(uow.Connection, householdId, userId, ToFields(request), now, ct);
        await SyncBankNameRulesAsync(imports, uow.Connection, householdId, row.Id, [], row.BankNames, row.BankNamesLearned, now, ct);
        await activity.RecordAsync(uow, householdId, userId, "recurring.created", "recurring", row.Id,
            ActivitySummaries.RecurringCreated(row, currency),
            new { itemId = row.Id, kind = row.Kind.ToDb(), amountCents = row.AmountCents, bankNames = row.BankNames, amountVaries = row.AmountVaries }, ct, principal.GetDisplayName());
        uow.AfterCommit(() => matching.Enqueue(householdId, MatchingJobKind.Rematch));
        await uow.CommitAsync(ct);

        return TypedResults.Created($"/v1/recurring/{row.Id}", row.ToDto(time.Today()));
    }

    private static async Task<Results<Ok<RecurringItemDto>, NotFound>> Get(Guid id, ClaimsPrincipal principal, RecurringRepository items, TimeProvider time, CancellationToken ct)
    {
        var row = await items.GetAsync(principal.GetHouseholdId(), id, ct);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(row.ToDto(time.Today()));
    }

    private static async Task<Results<Ok<RecurringItemDto>, NotFound, ValidationProblem>> Update(
        Guid id,
        RecurringRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        ImportRepository imports,
        CategoryRepository categories,
        AccountRepository accounts,
        ActivityService activity,
        MatchingQueue matching,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        if (await items.GetAsync(householdId, id, ct) is not { } existing)
        {
            return TypedResults.NotFound();
        }

        if (await CheckReferencesAsync(householdId, request, categories, accounts, ct) is { } problem)
        {
            return problem;
        }

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var now = time.Now();
        var row = await items.UpdateAsync(uow.Connection, householdId, id, ToFields(request), now, ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        await SyncBankNameRulesAsync(imports, uow.Connection, householdId, row.Id, existing.BankNames, row.BankNames, row.BankNamesLearned, now, ct);

        await activity.RecordAsync(uow, householdId, userId, "recurring.updated", "recurring", row.Id,
            ActivitySummaries.RecurringUpdated(row),
            new { itemId = row.Id, kind = row.Kind.ToDb(), amountCents = row.AmountCents, bankNames = row.BankNames, amountVaries = row.AmountVaries }, ct, principal.GetDisplayName());
        uow.AfterCommit(() => matching.Enqueue(householdId, MatchingJobKind.Rematch));
        await uow.CommitAsync(ct);
        return TypedResults.Ok(row.ToDto(time.Today()));
    }

    private static async Task<Results<NoContent, NotFound>> Archive(
        Guid id,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        ImportRepository imports,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await items.GetAsync(uow.Connection, householdId, id, ct);
        if (row is null || !await items.ArchiveAsync(uow.Connection, householdId, id, time.Now(), ct))
        {
            return TypedResults.NotFound();
        }

        // An archived item must not keep auto-applying bank transactions.
        await imports.ClearItemRuleAsync(uow.Connection, householdId, payeeNorm: null, row.Id, ct);

        await activity.RecordAsync(uow, householdId, userId, "recurring.deleted", "recurring", row.Id,
            ActivitySummaries.RecurringDeleted(row), new { itemId = row.Id, kind = row.Kind.ToDb() }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<OccurrenceDto>, NotFound>> MarkPaid(
        Guid id,
        DateOnly date,
        MarkPaidRequest? request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var item = await items.GetAsync(householdId, id, ct);
        if (item is null || !RecurrenceEngine.IsOccurrence(item.Rule, date))
        {
            return TypedResults.NotFound();
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        var amount = request?.AmountCents ?? item.AmountCents;

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var payment = await items.UpsertPaymentAsync(uow.Connection, item.Id, date, userId, amount, time.Now(), auto: false, PaymentSource.Manual, ct);
        await activity.RecordAsync(uow, householdId, userId, "occurrence.paid", "occurrence", item.Id,
            ActivitySummaries.OccurrencePaid(item.Name, amount, currency), new { itemId = item.Id, dueDate = date, amountCents = amount }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);

        return TypedResults.Ok(item.ToOccurrence(date, payment, time.Today()));
    }

    private static async Task<Results<Ok<OccurrenceDto>, NotFound>> MarkUnpaid(
        Guid id,
        DateOnly date,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        InboxService inbox,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var item = await items.GetAsync(householdId, id, ct);
        if (item is null || !RecurrenceEngine.IsOccurrence(item.Rule, date))
        {
            return TypedResults.NotFound();
        }

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var removed = await items.DeletePaymentAsync(uow.Connection, item.Id, date, ct);
        if (removed is not null)
        {
            // v3: a payment that came from the inbox (confirmed or auto-matched) puts its transaction back to pending.
            var restored = await inbox.RestoreForOccurrenceAsync(uow.Connection, householdId, item.Id, date, ct);

            // Undo guard: once a user un-marks an autopay occurrence the scheduler must leave it alone. An auto-matched
            // (bank) payment is not autopay, so it only needs the skip when the item itself is autoPay.
            if (item.AutoPay || (removed.Auto && restored is null))
            {
                await items.InsertAutopaySkipAsync(uow.Connection, item.Id, date, userId, time.Now(), ct);
            }

            await activity.RecordAsync(uow, householdId, userId, "occurrence.unpaid", "occurrence", item.Id,
                ActivitySummaries.OccurrenceUnpaid(item.Name), new { itemId = item.Id, dueDate = date, wasAuto = removed.Auto, restoredTransactionId = restored?.Id }, ct, principal.GetDisplayName());
        }

        await uow.CommitAsync(ct);
        return TypedResults.Ok(item.ToOccurrence(date, null, time.Today()));
    }

    // ---------------------------------------------------------------------------------------------
    // v5
    // ---------------------------------------------------------------------------------------------

    public const int DefaultOccurrenceCandidateDays = 10;
    public const int MaxOccurrenceCandidateDays = 60;

    private static async Task<Results<Created<FromTransactionResponse>, ValidationProblem, ProblemHttpResult>> FromTransaction(
        FromTransactionRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        ImportRepository imports,
        CategoryRepository categories,
        AccountRepository accounts,
        HouseholdRepository households,
        InboxService inbox,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var actorName = principal.GetDisplayName();
        var currency = await households.CurrencyAsync(householdId, ct);
        var transactionId = request.TransactionId!.Value;

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        await imports.LockAsync(uow.Connection, householdId, transactionId, ct);
        var row = await imports.GetAsync(uow.Connection, householdId, transactionId, ct);
        if (row is null)
        {
            return Problems.Validation("transactionId", "Unknown transaction.");
        }

        if (row.IsHidden)
        {
            return Problems.Validation("transactionId", "This authorization was dropped or superseded by the bank.");
        }

        var kind = request.Kind ?? (row.IsOutflow ? Kind.Bill : Kind.Income);
        if (!TransactionMatcher.SignMatchesKind(kind, row.IsOutflow))
        {
            return Problems.Validation("kind", row.IsOutflow ? "Money out makes a bill or subscription." : "Money in makes an income item.");
        }

        var source = (string.IsNullOrWhiteSpace(row.Payee) ? row.Description : row.Payee).Trim();
        var name = string.IsNullOrWhiteSpace(request.Name) ? Truncate(source, 80) : request.Name.Trim();
        if (name.Length == 0)
        {
            name = "Bank transaction";
        }

        Guid? categoryId = kind.IsOutflow() ? row.ConfirmedCategoryId ?? row.SuggestedCategoryId : null;
        if (categoryId is { } c && !await categories.ExistsAsync(uow.Connection, householdId, c, ct))
        {
            categoryId = null;
        }

        Guid? accountId = row.AccountId is { } a && await accounts.ExistsAsync(uow.Connection, householdId, a, ct) ? a : null;
        var hint = Truncate(source, 80);
        var now = time.Now();
        var fields = new RecurringFields(name, kind, row.Magnitude, categoryId, accountId, request.Frequency ?? Frequency.Monthly,
            row.PostedOn, EndDate: null, AutoPay: false, Notes: null, Url: null, BankNames: BankNames.FromRequest(null, hint));
        var item = await items.CreateAsync(uow.Connection, householdId, userId, fields, now, ct);
        await SyncBankNameRulesAsync(imports, uow.Connection, householdId, item.Id, [], item.BankNames, [], now, ct);

        // The first occurrence is the transaction's own date; matching reverses whatever the row stood for before.
        var error = await inbox.MatchAsync(uow, householdId, userId, actorName, currency, row, item.Id, item.AnchorDate, quiet: true, ct);
        if (error is not null)
        {
            return error.Conflict ? Problems.Conflict("Occurrence already paid", error.Message) : Problems.Validation(error.Field, error.Message);
        }

        await activity.RecordAsync(uow, householdId, userId, "recurring.from_transaction", "recurring", item.Id,
            ActivitySummaries.RecurringFromTransaction(item, currency),
            new { itemId = item.Id, transactionId = row.Id, kind = item.Kind.ToDb(), amountCents = item.AmountCents, dueDate = item.AnchorDate, previousStatus = row.Status }, ct, actorName);

        var fresh = await imports.GetAsync(uow.Connection, householdId, row.Id, ct) ?? row;
        var freshItem = await items.GetAsync(uow.Connection, householdId, item.Id, ct) ?? item;
        await uow.CommitAsync(ct);
        return TypedResults.Created($"/v1/recurring/{item.Id}", new FromTransactionResponse(freshItem.ToDto(time.Today()), fresh.ToDto()));
    }

    private static async Task<Results<Ok<List<BillPaymentDto>>, NotFound, ValidationProblem>> Payments(
        Guid id,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        ImportRepository imports,
        TimeProvider time,
        CancellationToken ct,
        int limit = PaymentHistory.DefaultLimit)
    {
        if (limit is < 1 or > PaymentHistory.MaxLimit)
        {
            return Problems.Validation("limit", $"Must be between 1 and {PaymentHistory.MaxLimit}.");
        }

        var householdId = principal.GetHouseholdId();
        await using var conn = await db.OpenConnectionAsync(ct);
        var item = await items.GetAsync(conn, householdId, id, ct);
        if (item is null)
        {
            return TypedResults.NotFound();
        }

        var dates = PaymentHistory.Dates(item.Rule, time.Today(), limit);
        if (dates.Count == 0)
        {
            return TypedResults.Ok(new List<BillPaymentDto>());
        }

        var payments = (await items.ListItemPaymentsAsync(conn, householdId, item.Id, dates.Min(), dates.Max(), ct)).ToDictionary(p => p.DueDate);
        var transactions = (await imports.ListConfirmedForItemAsync(conn, householdId, item.Id, ct))
            .Where(t => t.ConfirmedDueDate is not null)
            .GroupBy(t => t.ConfirmedDueDate!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var result = dates.Select(d =>
        {
            var paid = payments.GetValueOrDefault(d);
            var tx = transactions.GetValueOrDefault(d);
            return new BillPaymentDto(
                d,
                item.AmountCents,
                paid is null ? null : new PaidInfo(paid.PaidAt, paid.PaidBy, paid.AmountCents, paid.Auto, paid.Source),
                WeekEngine.Variance(paid?.AmountCents, item.AmountCents),
                tx is null ? null : new BillPaymentTransactionDto(tx.Id, tx.PostedOn, tx.AmountCents, tx.Description, tx.AccountName));
        }).ToList();
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<IReadOnlyList<InboxItemDto>>, NotFound, ValidationProblem>> OccurrenceCandidates(
        Guid id,
        DateOnly date,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        InboxService inbox,
        CancellationToken ct,
        int days = DefaultOccurrenceCandidateDays)
    {
        if (days is < 1 or > MaxOccurrenceCandidateDays)
        {
            return Problems.Validation("days", $"Must be between 1 and {MaxOccurrenceCandidateDays}.");
        }

        var householdId = principal.GetHouseholdId();
        await using var conn = await db.OpenConnectionAsync(ct);
        var item = await items.GetAsync(conn, householdId, id, ct);
        if (item is null || !RecurrenceEngine.IsOccurrence(item.Rule, date))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await inbox.TransactionCandidatesAsync(conn, householdId, item, date, days, ct));
    }

    private static async Task<Results<Ok<OccurrenceDto>, NotFound, ValidationProblem, ProblemHttpResult>> LinkTransaction(
        Guid id,
        DateOnly date,
        LinkTransactionRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        ImportRepository imports,
        HouseholdRepository households,
        InboxService inbox,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var currency = await households.CurrencyAsync(householdId, ct);
        var transactionId = request.TransactionId!.Value;

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var item = await items.GetAsync(uow.Connection, householdId, id, ct);
        if (item is null || !RecurrenceEngine.IsOccurrence(item.Rule, date))
        {
            return TypedResults.NotFound();
        }

        await imports.LockAsync(uow.Connection, householdId, transactionId, ct);
        var row = await imports.GetAsync(uow.Connection, householdId, transactionId, ct);
        if (row is null)
        {
            return Problems.Validation("transactionId", "Unknown transaction.");
        }

        if (row.IsHidden)
        {
            return Problems.Validation("transactionId", "This authorization was dropped or superseded by the bank.");
        }

        var error = await inbox.MatchAsync(uow, householdId, principal.GetUserId(), principal.GetDisplayName(), currency, row, item.Id, date, quiet: false, ct);
        if (error is { Conflict: true })
        {
            return Problems.Conflict("Occurrence already paid", error.Message);
        }

        if (error is not null)
        {
            return Problems.Validation(error.Field == "itemId" ? "transactionId" : error.Field, error.Message);
        }

        var payment = await items.GetPaymentAsync(uow.Connection, item.Id, date, ct);
        await uow.CommitAsync(ct);
        return TypedResults.Ok(item.ToOccurrence(date, payment, time.Today()));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();

    /// <summary>categoryId / accountId must belong to the caller's household; otherwise a field-level 400 (not a 404, which is reserved for the route id).</summary>
    private static async Task<ValidationProblem?> CheckReferencesAsync(Guid householdId, RecurringRequest request, CategoryRepository categories, AccountRepository accounts, CancellationToken ct)
    {
        if (request.CategoryId is { } categoryId && !await categories.ExistsAsync(householdId, categoryId, ct))
        {
            return Problems.Validation("categoryId", "Unknown category.");
        }

        if (request.AccountId is { } accountId && !await accounts.ExistsAsync(householdId, accountId, ct))
        {
            return Problems.Validation("accountId", "Unknown account.");
        }

        return null;
    }

    /// <summary>
    /// v6 bank names → item payee rules (v4 payee hint semantics, per name): every new user name upserts
    /// <c>payee_rules(payee_norm(name), item_id)</c> so the built-in matcher can auto-apply it; a removed name's rule is cleared when it
    /// still points at this item and no remaining user or learned name has the same normalised form. Names that normalise to nothing
    /// are ignored.
    /// </summary>
    internal static async Task SyncBankNameRulesAsync(
        ImportRepository imports,
        NpgsqlConnection conn,
        Guid householdId,
        Guid itemId,
        IEnumerable<string> previousNames,
        IEnumerable<string> names,
        IEnumerable<string> learnedNames,
        DateTime now,
        CancellationToken ct)
    {
        var before = previousNames.Select(Norm).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var after = names.Select(Norm).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var learned = learnedNames.Select(Norm).OfType<string>().ToHashSet(StringComparer.Ordinal);

        foreach (var removed in before.Where(n => !after.Contains(n) && !learned.Contains(n)))
        {
            await imports.ClearItemRuleAsync(conn, householdId, removed, itemId, ct);
        }

        foreach (var added in after.Where(n => !before.Contains(n)))
        {
            await imports.SetItemRuleAsync(conn, householdId, added, itemId, now, ct);
        }
    }

    private static string? Norm(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : TransactionMatcher.NormalisePayee(value, null) is { Length: > 0 } n ? n : null;

    /// <summary>
    /// v6 <c>DELETE /recurring/{id}/bank-names/learned/{index}</c>: removes one learned name (and its item payee rule unless a user name
    /// normalises the same), then re-scores rows to review.
    /// </summary>
    private static async Task<Results<Ok<RecurringItemDto>, NotFound>> ForgetLearnedName(
        Guid id,
        int index,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        ImportRepository imports,
        ActivityService activity,
        MatchingQueue matching,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var item = await items.GetAsync(uow.Connection, householdId, id, ct);
        if (item is null || index < 0 || index >= item.BankNamesLearned.Length)
        {
            return TypedResults.NotFound();
        }

        var name = item.BankNamesLearned[index];
        var remaining = item.BankNamesLearned.Where((_, i) => i != index).ToList();
        var now = time.Now();
        await items.SetLearnedNamesAsync(uow.Connection, householdId, item.Id, remaining, now, ct);
        if (Norm(name) is { } norm && !item.BankNames.Concat(remaining).Any(n => Norm(n) == norm))
        {
            await imports.ClearItemRuleAsync(uow.Connection, householdId, norm, item.Id, ct);
        }

        await activity.RecordAsync(uow, householdId, principal.GetUserId(), "recurring.updated", "recurring", item.Id,
            ActivitySummaries.BankNameForgotten(item, name), new { itemId = item.Id, forgotLearnedName = name }, ct, principal.GetDisplayName());
        var fresh = await items.GetAsync(uow.Connection, householdId, item.Id, ct) ?? item;
        uow.AfterCommit(() => matching.Enqueue(householdId, MatchingJobKind.Rematch));
        await uow.CommitAsync(ct);
        return TypedResults.Ok(fresh.ToDto(time.Today()));
    }

    private static RecurringFields ToFields(RecurringRequest r) => new(
        r.Name.Trim(),
        r.Kind!.Value,
        r.AmountCents,
        r.CategoryId,
        r.AccountId,
        r.Frequency!.Value,
        r.AnchorDate!.Value,
        r.EndDate,
        r.AutoPay ?? false,
        string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
        string.IsNullOrWhiteSpace(r.Url) ? null : r.Url.Trim(),
        BankNames.FromRequest(r.BankNames, r.PayeeHint),
        r.AmountVaries ?? false);
}
