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

/// <summary>Monthly budgets (v2) and, with <c>?period=weekly</c>, weekly budgets plus the weekly budget view (v4).</summary>
public static class BudgetEndpoints
{
    public static RouteGroupBuilder MapBudgets(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/budgets").WithTags("Budgets").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Monthly budget rows for a month: every category with recurring + expense spend vs. its budget");
        group.MapGet("/week", Week).WithSummary("Weekly budget rows for the week containing ?start (default: this week): overall target, budgeted categories, 8-week history");
        group.MapPut("/{categoryId:guid}", Set).WithValidation<BudgetRequest>()
            .WithSummary("Set a category's budget. ?period=monthly (default) returns its row for ?month; ?period=weekly returns its WeekBudgetRow for the current week");
        group.MapDelete("/{categoryId:guid}", Clear).WithSummary("Remove a category's budget for ?period (default monthly)");

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

    private static async Task<Results<Ok<WeekBudgetDto>, NotFound>> Week(
        ClaimsPrincipal principal,
        WeekService weeks,
        CancellationToken ct,
        DateOnly? start = null)
    {
        var view = await weeks.GetAsync(principal.GetHouseholdId(), start, ct);
        return view is null ? TypedResults.NotFound() : TypedResults.Ok(view.Budget);
    }

    private static async Task<Results<Ok<BudgetRowDto>, Ok<WeekBudgetRowDto>, NotFound, ValidationProblem>> Set(
        Guid categoryId,
        BudgetRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        BudgetRepository budgetStore,
        CategoryRepository categories,
        HouseholdRepository households,
        SummaryService summary,
        BudgetService budgets,
        WeekService weeks,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct,
        string? month = null,
        string? period = null)
    {
        if (!TryParsePeriod(period, out var budgetPeriod))
        {
            return Problems.Validation("period", "Must be 'monthly' or 'weekly'.");
        }

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
            await budgetStore.UpsertAsync(uow.Connection, householdId, categoryId, budgetPeriod, request.AmountCents, time.Now(), ct);
            await activity.RecordAsync(uow, householdId, userId, "budget.set", "budget", categoryId,
                ActivitySummaries.BudgetSet(category.Name, request.AmountCents, currency, budgetPeriod),
                new { categoryId, amountCents = request.AmountCents, period = budgetPeriod.ToDb() }, ct, principal.GetDisplayName());
            await uow.CommitAsync(ct);
        }

        if (budgetPeriod == BudgetPeriod.Weekly)
        {
            var view = await weeks.GetAsync(householdId, null, ct);
            var weekRow = view?.Budget.Categories.FirstOrDefault(r => r.CategoryId == categoryId);
            return weekRow is null ? TypedResults.NotFound() : TypedResults.Ok(weekRow);
        }

        var data = await summary.LoadMonthAsync(householdId, monthStart, ct);
        var rows = await budgets.RowsAsync(householdId, data, ct);
        var row = rows.FirstOrDefault(r => r.CategoryId == categoryId);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(row);
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> Clear(
        Guid categoryId,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        BudgetRepository budgetStore,
        CategoryRepository categories,
        ActivityService activity,
        CancellationToken ct,
        string? period = null)
    {
        if (!TryParsePeriod(period, out var budgetPeriod))
        {
            return Problems.Validation("period", "Must be 'monthly' or 'weekly'.");
        }

        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var category = await categories.GetAsync(householdId, categoryId, ct);
        if (category is null)
        {
            return TypedResults.NotFound();
        }

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        if (!await budgetStore.DeleteAsync(uow.Connection, householdId, categoryId, budgetPeriod, ct))
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "budget.cleared", "budget", categoryId,
            ActivitySummaries.BudgetCleared(category.Name, budgetPeriod), new { categoryId, period = budgetPeriod.ToDb() }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary><c>?period=</c> is case-insensitive and defaults to monthly.</summary>
    public static bool TryParsePeriod(string? period, out BudgetPeriod result)
    {
        if (string.IsNullOrWhiteSpace(period))
        {
            result = BudgetPeriod.Monthly;
            return true;
        }

        // Enum.TryParse also accepts numbers ("1"); only the names are valid query values.
        if (!char.IsDigit(period.Trim()[0]) && Enum.TryParse(period.Trim(), ignoreCase: true, out result) && Enum.IsDefined(result))
        {
            return true;
        }

        result = BudgetPeriod.Monthly;
        return false;
    }
}
