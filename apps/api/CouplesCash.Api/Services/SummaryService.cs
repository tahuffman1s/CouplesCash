using System.Globalization;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Services;

/// <summary>Everything a dashboard month needs, computed once and shared by <c>/summary</c> and <c>/reports/month</c>.</summary>
public sealed record MonthData(
    DateOnly MonthStart,
    DateOnly MonthEnd,
    IReadOnlyList<AccountRow> Accounts,
    IReadOnlyList<RecurringItemRow> Items,
    IReadOnlyList<RecurringItemRow> ActiveItems,
    IReadOnlyList<OccurrenceDto> Occurrences,
    IReadOnlyList<ExpenseRow> Expenses,
    IReadOnlyList<CategoryRow> Categories);

/// <summary>
/// Dashboard numbers for a month. v2: one-off expenses count as spent outflow, so <c>byCategory</c>,
/// <c>dueThisMonth</c> and <c>paidThisMonth</c> include the month's expenses (an expense is due and paid
/// the moment it is recorded); <c>remainingThisMonth</c> stays "unpaid occurrences".
/// </summary>
public sealed class SummaryService(
    AccountRepository accounts,
    RecurringRepository items,
    ExpenseRepository expenses,
    CategoryRepository categories,
    OccurrenceService occurrences)
{
    public const string UncategorizedName = "Uncategorized";
    public const string UncategorizedColor = "#9ca3af";
    private const int UpcomingDays = 14;
    private const int UpcomingMax = 20;

    public static bool TryParseMonth(string? month, DateOnly today, out DateOnly monthStart)
    {
        if (month is null)
        {
            monthStart = new DateOnly(today.Year, today.Month, 1);
            return true;
        }

        return DateOnly.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out monthStart);
    }

    public async Task<MonthData> LoadMonthAsync(Guid householdId, DateOnly monthStart, CancellationToken ct)
    {
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var accountRows = await accounts.ListAsync(householdId, ct);
        var itemRows = await items.ListAsync(householdId, null, ct);
        var monthOccurrences = await occurrences.ListAsync(householdId, itemRows, monthStart, monthEnd, ct);
        var expenseRows = await expenses.ListAsync(householdId, monthStart, monthEnd, ct);
        var categoryRows = await categories.ListAsync(householdId, ct);
        var active = itemRows.Where(i => BudgetMath.IsActiveInMonth(i.AnchorDate, i.EndDate, monthStart)).ToList();
        return new MonthData(monthStart, monthEnd, accountRows, itemRows, active, monthOccurrences, expenseRows, categoryRows);
    }

    public async Task<SummaryDto> BuildAsync(Guid householdId, MonthData data, DateOnly today, CancellationToken ct)
    {
        var upcomingWindow = await occurrences.ListAsync(householdId, data.Items, today, today.AddDays(UpcomingDays), ct);

        // Balances. Credit/loan are "amount owed" and subtract.
        var assets = data.Accounts.Where(a => !a.Type.IsLiability()).Sum(a => a.BalanceCents);
        var liabilities = data.Accounts.Where(a => a.Type.IsLiability()).Sum(a => a.BalanceCents);

        var active = data.ActiveItems;
        long MonthlyFor(Kind kind) => active.Where(i => i.Kind == kind).Sum(i => RecurrenceEngine.MonthlyEquivalent(i.AmountCents, i.Frequency));

        // v7 transfers are not bills, subscriptions or income: byKind keeps its three v1 entries.
        var byKind = Enum.GetValues<Kind>()
            .Where(kind => kind != Kind.Transfer)
            .Select(kind => new KindBreakdown(kind, MonthlyFor(kind), active.Count(i => i.Kind == kind)))
            .ToList();

        var byCategory = ByCategory(active, data.Expenses, data.Categories);

        // What is actually due this month (outflows only), what has been paid, and what is left.
        var outflows = data.Occurrences.Where(o => o.Kind.IsOutflow()).ToList();
        var expensesTotal = data.Expenses.Sum(e => e.AmountCents);
        var due = outflows.Sum(o => o.AmountCents) + expensesTotal;
        var paid = outflows.Where(o => o.Paid is not null).Sum(o => o.Paid!.AmountCents) + expensesTotal;
        var remaining = outflows.Where(o => o.Paid is null).Sum(o => o.AmountCents);

        var upcoming = upcomingWindow
            .OrderBy(o => o.Paid is null ? 0 : 1)
            .ThenBy(o => o.DueDate)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Take(UpcomingMax)
            .ToList();

        return new SummaryDto(
            Month: data.MonthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
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
            Accounts: data.Accounts.Select(a => a.ToDto()).ToList());
    }

    /// <summary>Monthly-equivalent outflow per category plus the month's expenses, with a grey "Uncategorized" bucket.</summary>
    private static List<CategoryBreakdown> ByCategory(IReadOnlyList<RecurringItemRow> active, IReadOnlyList<ExpenseRow> monthExpenses, IReadOnlyList<CategoryRow> categories)
    {
        var lookup = categories.ToDictionary(c => c.Id);

        var entries = active.Where(i => i.Kind.IsOutflow())
            .Select(i => new CategoryAmount(i.CategoryId, RecurrenceEngine.MonthlyEquivalent(i.AmountCents, i.Frequency)))
            .Concat(monthExpenses.Select(e => new CategoryAmount(e.CategoryId, e.AmountCents)));

        return entries
            .GroupBy(e => e.CategoryId)
            .Select(g =>
            {
                var category = g.Key is { } id && lookup.TryGetValue(id, out var c) ? c : null;
                return new CategoryBreakdown(
                    category?.Id,
                    category?.Name ?? UncategorizedName,
                    category?.Color ?? UncategorizedColor,
                    g.Sum(e => e.Cents),
                    g.Count());
            })
            .GroupBy(c => c.CategoryId)
            .Select(g => g.Count() == 1 ? g.First() : new CategoryBreakdown(g.Key, g.First().Name, g.First().Color, g.Sum(c => c.AmountCents), g.Sum(c => c.Count)))
            .OrderByDescending(c => c.AmountCents)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
