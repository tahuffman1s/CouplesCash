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

public static class SummaryEndpoints
{
    private const string UncategorizedName = "Uncategorized";
    private const string UncategorizedColor = "#9ca3af";
    private const int UpcomingDays = 14;
    private const int UpcomingMax = 20;

    public static RouteGroupBuilder MapSummary(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/summary").WithTags("Summary").RequireAuthorization().RequireHousehold();
        group.MapGet("", GetSummary).WithSummary("Dashboard numbers for a month (default: current)");
        return group;
    }

    private static async Task<Results<Ok<SummaryDto>, ValidationProblem>> GetSummary(
        ClaimsPrincipal principal,
        AccountRepository accounts,
        RecurringRepository items,
        OccurrenceService occurrences,
        TimeProvider time,
        CancellationToken ct,
        string? month = null)
    {
        var today = time.Today();
        DateOnly monthStart;
        if (month is null)
        {
            monthStart = new DateOnly(today.Year, today.Month, 1);
        }
        else if (!DateOnly.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out monthStart))
        {
            return Problems.Validation("month", "Must be YYYY-MM.");
        }

        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var householdId = principal.GetHouseholdId();

        var accountRows = await accounts.ListAsync(householdId, ct);
        var itemRows = await items.ListAsync(householdId, null, ct);
        var monthOccurrences = await occurrences.ListAsync(householdId, itemRows, monthStart, monthEnd, ct);
        var upcomingWindow = await occurrences.ListAsync(householdId, itemRows, today, today.AddDays(UpcomingDays), ct);

        // Balances. Credit/loan are "amount owed" and subtract.
        var assets = accountRows.Where(a => !a.Type.IsLiability()).Sum(a => a.BalanceCents);
        var liabilities = accountRows.Where(a => a.Type.IsLiability()).Sum(a => a.BalanceCents);

        // Monthly equivalents of items still active this month (not ended before the month started).
        var active = itemRows.Where(i => i.EndDate is null || i.EndDate >= monthStart).ToList();
        long MonthlyFor(Kind kind) => active.Where(i => i.Kind == kind).Sum(i => RecurrenceEngine.MonthlyEquivalent(i.AmountCents, i.Frequency));

        var byKind = Enum.GetValues<Kind>()
            .Select(kind => new KindBreakdown(kind, MonthlyFor(kind), active.Count(i => i.Kind == kind)))
            .ToList();

        var byCategory = active
            .Where(i => i.Kind.IsOutflow())
            .GroupBy(i => i.CategoryId)
            .Select(g => new CategoryBreakdown(
                g.Key,
                g.Key is null ? UncategorizedName : g.First().CategoryName ?? UncategorizedName,
                g.Key is null ? UncategorizedColor : g.First().CategoryColor ?? UncategorizedColor,
                g.Sum(i => RecurrenceEngine.MonthlyEquivalent(i.AmountCents, i.Frequency)),
                g.Count()))
            .OrderByDescending(c => c.AmountCents)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // What is actually due this month (outflows only), what has been paid, and what is left.
        var outflows = monthOccurrences.Where(o => o.Kind.IsOutflow()).ToList();
        var due = outflows.Sum(o => o.AmountCents);
        var paid = outflows.Where(o => o.Paid is not null).Sum(o => o.Paid!.AmountCents);
        var remaining = outflows.Where(o => o.Paid is null).Sum(o => o.AmountCents);

        var upcoming = upcomingWindow
            .OrderBy(o => o.Paid is null ? 0 : 1)
            .ThenBy(o => o.DueDate)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Take(UpcomingMax)
            .ToList();

        return TypedResults.Ok(new SummaryDto(
            Month: monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            NetWorthCents: assets - liabilities,
            AssetsCents: assets,
            LiabilitiesCents: liabilities,
            MonthlyBillsCents: MonthlyFor(Kind.Bill),
            MonthlySubscriptionsCents: MonthlyFor(Kind.Subscription),
            MonthlyIncomeCents: MonthlyFor(Kind.Income),
            DueThisMonthCents: due,
            PaidThisMonthCents: paid,
            RemainingThisMonthCents: remaining,
            ByCategory: byCategory,
            ByKind: byKind,
            Upcoming: upcoming,
            Accounts: accountRows.Select(a => a.ToDto()).ToList()));
    }
}
