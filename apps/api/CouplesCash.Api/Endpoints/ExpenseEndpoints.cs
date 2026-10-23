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

/// <summary>
/// One-off expenses. "Deducting" adjusts the linked account in the same transaction and records a balance
/// snapshot: assets go down by the amount, liabilities (credit/loan, stored as amount owed) go up.
/// </summary>
public static class ExpenseEndpoints
{
    public static RouteGroupBuilder MapExpenses(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/expenses").WithTags("Expenses").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Expenses of a month (?month=YYYY-MM, default current) or a range (?from&to, ≤ 93 days), newest first");
        group.MapPost("", Create).WithValidation<ExpenseRequest>().WithSummary("Record an expense (deducts from the account by default when one is given)");
        group.MapGet("/{id:guid}", Get).WithSummary("Get an expense");
        group.MapPut("/{id:guid}", Update).WithValidation<ExpenseRequest>().WithSummary("Replace an expense (re-applies the deduction delta)");
        group.MapDelete("/{id:guid}", Delete).WithSummary("Delete an expense (reverses the deduction)");

        return group;
    }

    private static async Task<Results<Ok<List<ExpenseDto>>, ValidationProblem>> List(
        ClaimsPrincipal principal,
        ExpenseRepository expenses,
        TimeProvider time,
        CancellationToken ct,
        string? month = null,
        DateOnly? from = null,
        DateOnly? to = null)
    {
        DateOnly start, end;
        if (from is not null || to is not null)
        {
            if (from is null || to is null)
            {
                return Problems.Validation(from is null ? "from" : "to", "Both from and to are required (YYYY-MM-DD).");
            }

            if (to < from)
            {
                return Problems.Validation("to", "Must be on or after 'from'.");
            }

            if (to.Value.DayNumber - from.Value.DayNumber + 1 > CalendarEndpoints.MaxRangeDays)
            {
                return Problems.Validation("to", $"Range must be at most {CalendarEndpoints.MaxRangeDays} days.");
            }

            (start, end) = (from.Value, to.Value);
        }
        else
        {
            if (!SummaryService.TryParseMonth(month, time.Today(), out start))
            {
                return Problems.Validation("month", "Must be YYYY-MM.");
            }

            end = start.AddMonths(1).AddDays(-1);
        }

        var rows = await expenses.ListAsync(principal.GetHouseholdId(), start, end, ct);
        return TypedResults.Ok(rows.Select(e => e.ToDto()).ToList());
    }

    private static async Task<Results<Ok<ExpenseDto>, NotFound>> Get(Guid id, ClaimsPrincipal principal, ExpenseRepository expenses, CancellationToken ct)
    {
        var row = await expenses.GetAsync(principal.GetHouseholdId(), id, ct);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(row.ToDto());
    }

    private static async Task<Results<Created<ExpenseDto>, ValidationProblem>> Create(
        ExpenseRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        ExpenseRepository expenses,
        AccountRepository accounts,
        CategoryRepository categories,
        HouseholdRepository households,
        ActivityService activity,
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
        var fields = ToFields(request);
        var now = time.Now();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        if (fields.Deducted)
        {
            var account = await accounts.GetAsync(uow.Connection, householdId, fields.AccountId!.Value, ct);
            if (account is null)
            {
                return Problems.Validation("accountId", "Unknown account.");
            }

            await accounts.AdjustBalanceAsync(uow.Connection, householdId, account.Id, userId, Delta(account.Type, -fields.AmountCents), now, ct);
        }

        var row = await expenses.CreateAsync(uow.Connection, householdId, userId, fields, now, ct);
        await activity.RecordAsync(uow, householdId, userId, "expense.created", "expense", row.Id,
            ActivitySummaries.ExpenseCreated(row, currency), Data(row), ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);

        return TypedResults.Created($"/v1/expenses/{row.Id}", row.ToDto());
    }

    private static async Task<Results<Ok<ExpenseDto>, NotFound, ValidationProblem>> Update(
        Guid id,
        ExpenseRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        ExpenseRepository expenses,
        AccountRepository accounts,
        CategoryRepository categories,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        if (await expenses.GetAsync(householdId, id, ct) is null)
        {
            return TypedResults.NotFound();
        }

        if (await CheckReferencesAsync(householdId, request, categories, accounts, ct) is { } problem)
        {
            return problem;
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        var fields = ToFields(request);
        var now = time.Now();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var existing = await expenses.GetAsync(uow.Connection, householdId, id, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        // Net the old deduction against the new one per account, so moving an expense between accounts
        // restores one and charges the other, and a same-account amount change applies only the difference.
        var deltas = new Dictionary<Guid, long>();
        if (existing.Deducted && existing.AccountId is { } oldAccount)
        {
            deltas[oldAccount] = deltas.GetValueOrDefault(oldAccount) + existing.AmountCents; // reverse: give it back
        }

        if (fields.Deducted)
        {
            var newAccount = fields.AccountId!.Value;
            deltas[newAccount] = deltas.GetValueOrDefault(newAccount) - fields.AmountCents;
        }

        foreach (var (accountId, spendDelta) in deltas.Where(d => d.Value != 0))
        {
            var account = await accounts.GetAsync(uow.Connection, householdId, accountId, ct);
            if (account is null)
            {
                // The old account was archived in the meantime: nothing to restore to.
                continue;
            }

            await accounts.AdjustBalanceAsync(uow.Connection, householdId, accountId, userId, Delta(account.Type, spendDelta), now, ct);
        }

        var row = await expenses.UpdateAsync(uow.Connection, householdId, id, fields, now, ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "expense.updated", "expense", row.Id,
            ActivitySummaries.ExpenseUpdated(row, currency), Data(row), ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.Ok(row.ToDto());
    }

    private static async Task<Results<NoContent, NotFound>> Delete(
        Guid id,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        ExpenseRepository expenses,
        AccountRepository accounts,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var currency = await households.CurrencyAsync(householdId, ct);
        var now = time.Now();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await expenses.DeleteAsync(uow.Connection, householdId, id, ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        if (row.Deducted && row.AccountId is { } accountId)
        {
            var account = await accounts.GetAsync(uow.Connection, householdId, accountId, ct);
            if (account is not null)
            {
                await accounts.AdjustBalanceAsync(uow.Connection, householdId, accountId, userId, Delta(account.Type, row.AmountCents), now, ct);
            }
        }

        await activity.RecordAsync(uow, householdId, userId, "expense.deleted", "expense", row.Id,
            ActivitySummaries.ExpenseDeleted(row, currency), Data(row), ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Spending <paramref name="spendDelta"/> (negative = money out) moves an asset down and a liability (amount owed) up.</summary>
    internal static long Delta(AccountType type, long spendDelta) => type.IsLiability() ? -spendDelta : spendDelta;

    private static async Task<ValidationProblem?> CheckReferencesAsync(Guid householdId, ExpenseRequest request, CategoryRepository categories, AccountRepository accounts, CancellationToken ct)
    {
        if (request.CategoryId is { } categoryId && !await categories.ExistsAsync(householdId, categoryId, ct))
        {
            return Problems.Validation("categoryId", "Unknown category.");
        }

        if (request.AccountId is { } accountId && !await accounts.ExistsAsync(householdId, accountId, ct))
        {
            return Problems.Validation("accountId", "Unknown account.");
        }

        if (request.Deduct == true && request.AccountId is null)
        {
            return Problems.Validation("deduct", "An account is required to deduct from.");
        }

        return null;
    }

    private static ExpenseFields ToFields(ExpenseRequest r) => new(
        r.Name.Trim(),
        r.AmountCents,
        r.CategoryId,
        r.AccountId,
        r.SpentOn!.Value,
        string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim(),
        Deducted: r.AccountId is not null && (r.Deduct ?? true));

    private static object Data(ExpenseRow e) => new { expenseId = e.Id, amountCents = e.AmountCents, categoryId = e.CategoryId, accountId = e.AccountId, spentOn = e.SpentOn, deducted = e.Deducted };
}
