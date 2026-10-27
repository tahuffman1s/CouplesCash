using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace CouplesCash.Api.Endpoints;

/// <summary>v6: the Transactions tab feed (every account) and pending transactions grouped by account.</summary>
public static class TransactionEndpoints
{
    public static RouteGroupBuilder MapTransactions(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("").WithTags("Transactions").RequireAuthorization().RequireHousehold();

        group.MapGet("/transactions", Feed)
            .WithSummary("Transactions feed: pending (every in-progress row, first page only), posted rows + manual expenses newest first; ?accountId=&q=&before=&limit≤200");
        group.MapGet("/pending", Pending)
            .WithSummary("Pending transactions grouped by account (accounts with pending first, by pending outflow), with totals");
        group.MapPost("/transactions", Add).WithValidation<AddTransactionRequest>()
            .WithSummary("Add a transaction by hand (pending until the bank's own row replaces it); 409 when the bank already shows it, unless force");
        group.MapDelete("/transactions/{id:guid}", Remove)
            .WithSummary("Delete a transaction added by hand (bank rows: 404)");

        return group;
    }

    private static async Task<Results<Ok<TransactionFeedDto>, ValidationProblem>> Feed(
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        AccountRepository accounts,
        ImportRepository imports,
        CancellationToken ct,
        Guid? accountId = null,
        string? q = null,
        string? before = null,
        int limit = InboxEndpoints.DefaultLimit)
    {
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (search is { Length: > AccountEndpoints.MaxSearchLength })
        {
            return Problems.Validation("q", $"At most {AccountEndpoints.MaxSearchLength} characters.");
        }

        InboxCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(before))
        {
            if (!InboxEndpoints.TryParseCursor(before, out var parsed))
            {
                return Problems.Validation("before", "Must be a nextBefore value from a previous page.");
            }

            cursor = parsed;
        }

        var householdId = principal.GetHouseholdId();
        if (accountId is { } a && !await accounts.ExistsAsync(householdId, a, ct))
        {
            return Problems.Validation("accountId", "Unknown account.");
        }

        var size = Math.Clamp(limit, 1, InboxEndpoints.MaxLimit);
        var pending = cursor is null
            ? (await imports.ListBankPendingAsync(householdId, accountId, search, reviewOrConfirmedOnly: false, ct)).Select(r => r.ToAccountTransaction()).ToList()
            : [];
        var rows = await imports.ListAccountTransactionsAsync(householdId, new AccountTransactionQuery(accountId, "posted", search, null, null, false), cursor, size, ct);
        var items = rows.Take(size).Select(r => r.ToAccountTransaction()).ToList();
        var nextBefore = rows.Count > size ? InboxEndpoints.Cursor(rows[size - 1]) : null;

        await using var conn = await db.OpenConnectionAsync(ct);
        var toReview = await imports.CountPendingAsync(conn, householdId, ct);
        return TypedResults.Ok(new TransactionFeedDto(pending, items, nextBefore, toReview));
    }

    private static async Task<Results<Created<AccountTransactionDto>, ValidationProblem, ProblemHttpResult>> Add(
        AddTransactionRequest request,
        ClaimsPrincipal principal,
        AddedTransactionService added,
        CancellationToken ct)
    {
        var outcome = await added.AddAsync(principal.GetHouseholdId(), principal.GetUserId(), principal.GetDisplayName(), request.AccountId,
            request.AmountCents, request.Description, request.PostedOn, request.Force ?? false, ct);
        if (outcome.UnknownAccount)
        {
            return Problems.Validation("accountId", "Unknown account.");
        }

        if (outcome.Duplicate is { } d)
        {
            return TypedResults.Problem(
                title: "The bank already shows this",
                detail: $"{d.AccountName ?? "The bank"} already shows {InboxService.DisplayName(d)} {Money.Format(d.AmountCents, "USD")} on {d.PostedOn:yyyy-MM-dd}.",
                statusCode: StatusCodes.Status409Conflict,
                type: "likely_duplicate",
                extensions: new Dictionary<string, object?> { ["duplicate"] = d.ToAccountTransaction() });
        }

        var row = outcome.Row!;
        return TypedResults.Created($"/v1/transactions/{row.Id}", row.ToAccountTransaction());
    }

    private static async Task<Results<NoContent, NotFound>> Remove(Guid id, ClaimsPrincipal principal, AddedTransactionService added, CancellationToken ct) =>
        await added.DeleteAsync(principal.GetHouseholdId(), principal.GetUserId(), principal.GetDisplayName(), id, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();

    private static async Task<Ok<PendingViewDto>> Pending(
        ClaimsPrincipal principal,
        AccountRepository accounts,
        BankRepository banks,
        ImportRepository imports,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var accountRows = await accounts.ListAsync(householdId, ct);
        var externals = await banks.ListExternalAccountsAsync(householdId, ct);
        // v7: every visible pending row, whatever its review status — an auto-ignored pending transfer still leaves the account
        // (v6 listed pending|confirmed only, which is how "only one pending" could show while the bank sent three).
        var rows = await imports.ListBankPendingAsync(householdId, null, null, reviewOrConfirmedOnly: false, ct);

        var places = accountRows
            .Select(a => new PendingAccount(a.Id, null, a.Name, a.Institution, a.BalanceCents, a.AvailableCents, a.Linked))
            .Concat(externals.Where(e => e.AccountId is null)
                .Select(e => new PendingAccount(null, e.Id, e.Name, e.OrgName, e.BalanceCents, e.AvailableCents, Linked: false)))
            .ToList();
        var entries = rows.Select(r => new PendingEntry<InboxItemDto>(r.ToDto(), r.AccountId, r.ExternalAccountId, r.AmountCents)).ToList();
        var groups = PendingGrouping.Group(places, entries)
            .Select(b => new PendingGroupDto(b.Account.AccountId, b.Account.Name, b.Account.Institution, b.Account.BalanceCents, b.Account.AvailableCents,
                b.Count, b.OutCents, b.InCents, b.Items))
            .ToList();

        return TypedResults.Ok(new PendingViewDto(groups.Sum(g => g.PendingOutCents), groups.Sum(g => g.PendingInCents), groups.Sum(g => g.PendingCount), groups));
    }
}
