using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

public static class AccountEndpoints
{
    private const string DefaultColor = "#3b82f6";

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
        AccountRepository accounts,
        TimeProvider time,
        CancellationToken ct)
    {
        var row = await accounts.CreateAsync(principal.GetHouseholdId(), principal.GetUserId(), ToFields(request), time.Now(), ct);
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
        AccountRepository accounts,
        TimeProvider time,
        CancellationToken ct)
    {
        var row = await accounts.UpdateAsync(principal.GetHouseholdId(), id, principal.GetUserId(), ToFields(request), time.Now(), ct);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(row.ToDto());
    }

    private static async Task<Results<NoContent, NotFound>> Archive(Guid id, ClaimsPrincipal principal, AccountRepository accounts, TimeProvider time, CancellationToken ct) =>
        await accounts.ArchiveAsync(principal.GetHouseholdId(), id, time.Now(), ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();

    private static async Task<Results<Ok<AccountDto>, NotFound>> SetBalance(
        Guid id,
        BalanceRequest request,
        ClaimsPrincipal principal,
        AccountRepository accounts,
        TimeProvider time,
        CancellationToken ct)
    {
        var row = await accounts.SetBalanceAsync(principal.GetHouseholdId(), id, principal.GetUserId(), request.BalanceCents, time.Now(), ct);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(row.ToDto());
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

    private static string DefaultIcon(AccountType type) => type switch
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
