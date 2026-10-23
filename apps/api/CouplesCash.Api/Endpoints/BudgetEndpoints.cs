using System.Globalization;
using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace CouplesCash.Api.Endpoints;

public static class BudgetEndpoints
{
    public static RouteGroupBuilder MapBudgets(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/budgets").WithTags("Budgets").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Budget rows for a month: every category with recurring + expense spend vs. its budget");
        group.MapPut("/{categoryId:guid}", Set).WithValidation<BudgetRequest>().WithSummary("Set a category's monthly budget; returns its row for ?month (default current)");
        group.MapDelete("/{categoryId:guid}", Clear).WithSummary("Remove a category's budget");

        return group;
    }

    private static async Task<Results<Ok<BudgetsResponse>, ValidationProblem>> List(
        ClaimsPrincipal principal,
        SummaryService summary,
        BudgetService budgets,
        TimeProvider time,
        CancellationToken ct,
        string? month = null)
    {
        if (!SummaryService.TryParseMonth(month, time.Today(), out var monthStart))
        {
            return Problems.Validation("month", "Must be YYYY-MM.");
        }

        var householdId = principal.GetHouseholdId();
        var data = await summary.LoadMonthAsync(householdId, monthStart, ct);
        var rows = await budgets.RowsAsync(householdId, data, ct);
        var (totalBudget, totalSpent) = BudgetService.Totals(rows);
        return TypedResults.Ok(new BudgetsResponse(monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture), rows, totalBudget, totalSpent));
    }

    private static async Task<Results<Ok<BudgetRowDto>, NotFound, ValidationProblem>> Set(
        Guid categoryId,
        BudgetRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        BudgetRepository budgetStore,
        CategoryRepository categories,
        HouseholdRepository households,
        SummaryService summary,
        BudgetService budgets,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct,
        string? month = null)
    {
        if (!SummaryService.TryParseMonth(month, time.Today(), out var monthStart))
        {
            return Problems.Validation("month", "Must be YYYY-MM.");
        }

        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var category = await categories.GetAsync(householdId, categoryId, ct);
        if (category is null)
        {
            return TypedResults.NotFound();
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        await using (var uow = await db.BeginUnitOfWorkAsync(ct))
        {
            await budgetStore.UpsertAsync(uow.Connection, householdId, categoryId, request.AmountCents, time.Now(), ct);
            await activity.RecordAsync(uow, householdId, userId, "budget.set", "budget", categoryId,
                ActivitySummaries.BudgetSet(category.Name, request.AmountCents, currency), new { categoryId, amountCents = request.AmountCents }, ct, principal.GetDisplayName());
            await uow.CommitAsync(ct);
        }

        var data = await summary.LoadMonthAsync(householdId, monthStart, ct);
        var rows = await budgets.RowsAsync(householdId, data, ct);
        var row = rows.FirstOrDefault(r => r.CategoryId == categoryId);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(row);
    }

    private static async Task<Results<NoContent, NotFound>> Clear(
        Guid categoryId,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        BudgetRepository budgetStore,
        CategoryRepository categories,
        ActivityService activity,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var category = await categories.GetAsync(householdId, categoryId, ct);
        if (category is null)
        {
            return TypedResults.NotFound();
        }

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        if (!await budgetStore.DeleteAsync(uow.Connection, householdId, categoryId, ct))
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "budget.cleared", "budget", categoryId,
            ActivitySummaries.BudgetCleared(category.Name), new { categoryId }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }
}
