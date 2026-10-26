using System.Globalization;
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

public static class AccountEndpoints
{
    internal const string DefaultColor = "#3b82f6";

    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/accounts").WithTags("Accounts").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Active accounts ordered by type then name");
        group.MapPost("", Create).WithValidation<AccountRequest>().WithSummary("Create an account");
        group.MapGet("/{id:guid}", Get).WithSummary("Get an account");
        group.MapPut("/{id:guid}", Update).WithValidation<AccountRequest>().WithSummary("Replace an account");
        group.MapDelete("/{id:guid}", Archive).WithSummary("Archive (soft delete) an account");
        group.MapPost("/{id:guid}/balance", SetBalance).WithValidation<BalanceRequest>().WithSummary("Update the balance and record a snapshot");
        group.MapGet("/{id:guid}/history", History).WithSummary("Balance snapshots, newest first");
        group.MapGet("/{id:guid}/transactions", Transactions)
            .WithSummary("v5: bank rows and manual expenses on this account, newest first; ?filter=all|pending|review|matched|transfers&q=&month=YYYY-MM&before=&limit≤200&includeHidden=1");

        return group;
    }

    private static async Task<Ok<List<AccountDto>>> List(ClaimsPrincipal principal, AccountRepository accounts, CancellationToken ct)
    {
        var rows = await accounts.ListAsync(principal.GetHouseholdId(), ct);
        return TypedResults.Ok(rows.Select(a => a.ToDto()).ToList());
    }

    private static async Task<Created<AccountDto>> Create(
        AccountRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        AccountRepository accounts,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var currency = await households.CurrencyAsync(householdId, ct);

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await accounts.CreateAsync(uow.Connection, householdId, userId, ToFields(request), time.Now(), ct);
        await activity.RecordAsync(uow, householdId, userId, "account.created", "account", row.Id,
            ActivitySummaries.AccountCreated(row, currency), new { accountId = row.Id, balanceCents = row.BalanceCents }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);

        return TypedResults.Created($"/v1/accounts/{row.Id}", row.ToDto());
    }

    private static async Task<Results<Ok<AccountDto>, NotFound>> Get(Guid id, ClaimsPrincipal principal, AccountRepository accounts, CancellationToken ct)
    {
        var row = await accounts.GetAsync(principal.GetHouseholdId(), id, ct);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(row.ToDto());
    }

    private static async Task<Results<Ok<AccountDto>, NotFound>> Update(
        Guid id,
        AccountRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        AccountRepository accounts,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await accounts.UpdateAsync(uow.Connection, householdId, id, userId, ToFields(request), time.Now(), ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "account.updated", "account", row.Id,
            ActivitySummaries.AccountUpdated(row), new { accountId = row.Id, balanceCents = row.BalanceCents }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.Ok(row.ToDto());
    }

    private static async Task<Results<NoContent, NotFound>> Archive(
        Guid id,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        AccountRepository accounts,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await accounts.ArchiveAsync(uow.Connection, householdId, id, time.Now(), ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "account.deleted", "account", row.Id,
            ActivitySummaries.AccountDeleted(row), new { accountId = row.Id }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<AccountDto>, NotFound>> SetBalance(
        Guid id,
        BalanceRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        AccountRepository accounts,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var currency = await households.CurrencyAsync(householdId, ct);

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await accounts.SetBalanceAsync(uow.Connection, householdId, id, userId, request.BalanceCents, time.Now(), ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "account.balance", "account", row.Id,
            ActivitySummaries.AccountBalance(row, currency), new { accountId = row.Id, balanceCents = row.BalanceCents }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.Ok(row.ToDto());
    }

    private static async Task<Results<Ok<List<BalanceSnapshotDto>>, NotFound>> History(
        Guid id,
        ClaimsPrincipal principal,
        AccountRepository accounts,
        CancellationToken ct,
        int limit = 30)
    {
        var rows = await accounts.HistoryAsync(principal.GetHouseholdId(), id, Math.Clamp(limit, 1, 365), ct);
        return rows is null ? TypedResults.NotFound() : TypedResults.Ok(rows.Select(s => s.ToDto()).ToList());
    }

    public static readonly string[] TransactionFilters = ["all", "pending", "review", "matched", "transfers"];
    public const int MaxSearchLength = 100;

    private static async Task<Results<Ok<AccountTransactionsPage>, NotFound, ValidationProblem>> Transactions(
        Guid id,
        ClaimsPrincipal principal,
        AccountRepository accounts,
        ImportRepository imports,
        CancellationToken ct,
        string? filter = null,
        string? q = null,
        string? month = null,
        string? before = null,
        int limit = InboxEndpoints.DefaultLimit,
        string? includeHidden = null)
    {
        var mode = string.IsNullOrWhiteSpace(filter) ? "all" : filter.Trim().ToLowerInvariant();
        if (!TransactionFilters.Contains(mode, StringComparer.Ordinal))
        {
            return Problems.Validation("filter", "Must be one of: all, pending, review, matched, transfers.");
        }

        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (search is { Length: > MaxSearchLength })
        {
            return Problems.Validation("q", $"At most {MaxSearchLength} characters.");
        }

        DateOnly? from = null, to = null;
        if (!string.IsNullOrWhiteSpace(month))
        {
            if (!DateOnly.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            {
                return Problems.Validation("month", "Must be YYYY-MM.");
            }

            from = start;
            to = start.AddMonths(1).AddDays(-1);
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
        if (await accounts.GetAsync(householdId, id, ct) is null)
        {
            return TypedResults.NotFound();
        }

        var size = Math.Clamp(limit, 1, InboxEndpoints.MaxLimit);
        var query = new AccountTransactionQuery(id, mode, search, from, to, InboxEndpoints.IsTrue(includeHidden));
        var rows = await imports.ListAccountTransactionsAsync(householdId, query, cursor, size, ct);
        var totals = await imports.AccountTransactionTotalsAsync(householdId, query, ct);
        var page = rows.Take(size).Select(r => r.ToAccountTransaction()).ToList();
        var nextBefore = rows.Count > size ? InboxEndpoints.Cursor(rows[size - 1]) : null;
        return TypedResults.Ok(new AccountTransactionsPage(page, nextBefore,
            new AccountTransactionTotalsDto(totals.InCents, totals.OutCents, totals.PendingOutCents, totals.Count)));
    }

    private static AccountFields ToFields(AccountRequest r)
    {
        var type = r.Type!.Value;
        return new AccountFields(
            r.Name.Trim(),
            r.Institution.Trim(),
            type,
            r.BalanceCents,
            r.Color ?? DefaultColor,
            r.Icon ?? DefaultIcon(type),
            string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim());
    }

    internal static string DefaultIcon(AccountType type) => type switch
    {
        AccountType.Checking => "landmark",
        AccountType.Savings => "piggy-bank",
        AccountType.Credit => "credit-card",
        AccountType.Cash => "wallet",
        AccountType.Investment => "trending-up",
        AccountType.Loan => "hand-coins",
        _ => "wallet",
    };
}
