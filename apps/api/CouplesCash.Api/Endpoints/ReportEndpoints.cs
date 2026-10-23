using System.Globalization;
using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

public static class ReportEndpoints
{
    public const int TopExpenses = 5;

    public static RouteGroupBuilder MapReports(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/reports").WithTags("Reports").RequireAuthorization().RequireHousehold();
        group.MapGet("/month", Month).WithSummary("Month-close report: summary, net worth change, expenses, budgets, autopay list, paid/unpaid counts");
        return group;
    }

    private static async Task<Results<Ok<MonthReportDto>, ValidationProblem>> Month(
        ClaimsPrincipal principal,
        SummaryService summary,
        BudgetService budgets,
        AccountRepository accounts,
        TimeProvider time,
        CancellationToken ct,
        string? month = null)
    {
        var today = time.Today();
        if (!SummaryService.TryParseMonth(month, today, out var monthStart))
        {
            return Problems.Validation("month", "Must be YYYY-MM.");
        }

        var householdId = principal.GetHouseholdId();
        var data = await summary.LoadMonthAsync(householdId, monthStart, ct);
        var summaryDto = await summary.BuildAsync(householdId, data, today, ct);
        var budgetRows = await budgets.RowsAsync(householdId, data, ct);

        var previousNetWorth = await accounts.NetWorthBeforeAsync(householdId, monthStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), ct);
        var outflows = data.Occurrences.Where(o => o.Kind.IsOutflow()).ToList();

        return TypedResults.Ok(new MonthReportDto(
            Month: monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Summary: summaryDto,
            PreviousNetWorthCents: previousNetWorth,
            NetWorthDeltaCents: previousNetWorth is { } previous ? summaryDto.NetWorthCents - previous : null,
            ExpensesTotalCents: data.Expenses.Sum(e => e.AmountCents),
            TopExpenses: data.Expenses.OrderByDescending(e => e.AmountCents).ThenBy(e => e.SpentOn).Take(TopExpenses).Select(e => e.ToDto()).ToList(),
            Budgets: budgetRows,
            AutoPaid: data.Occurrences.Where(o => o.Paid is { Auto: true }).ToList(),
            PaidCount: outflows.Count(o => o.Paid is not null),
            UnpaidCount: outflows.Count(o => o.Paid is null),
            GeneratedAt: time.Now()));
    }
}
