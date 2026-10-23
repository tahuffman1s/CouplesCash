using CouplesCash.Api.Contracts;

namespace CouplesCash.Api.Domain;

/// <summary>What budget rows need to know about a category.</summary>
public readonly record struct BudgetCategory(Guid Id, string Name, string Color, string Icon);

/// <summary>A recurring item's share of a month: its category and monthly-equivalent amount.</summary>
public readonly record struct CategoryAmount(Guid? CategoryId, long Cents);

/// <summary>
/// Pure budget computation. <c>spent = recurring (monthly equivalents of bills + subscriptions active in
/// the month) + expenses (one-offs dated in the month)</c>. Every category gets a row; unbudgeted ones
/// have <c>budgetCents = remainingCents = percent = null</c>. Percent is a whole number and is not capped,
/// so over-budget categories read 120, 250, …
/// </summary>
public static class BudgetMath
{
    /// <summary>An item counts for a month when it has not ended before the month starts and starts no later than the month ends.</summary>
    public static bool IsActiveInMonth(DateOnly anchorDate, DateOnly? endDate, DateOnly monthStart)
    {
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        return (endDate is null || endDate >= monthStart) && anchorDate <= monthEnd;
    }

    public static IReadOnlyList<BudgetRowDto> Rows(
        IEnumerable<BudgetCategory> categories,
        IReadOnlyDictionary<Guid, long> budgets,
        IEnumerable<CategoryAmount> recurring,
        IEnumerable<CategoryAmount> expenses)
    {
        var recurringByCategory = Sum(recurring);
        var expensesByCategory = Sum(expenses);

        return categories.Select(c =>
        {
            var recurringCents = recurringByCategory.GetValueOrDefault(c.Id);
            var expensesCents = expensesByCategory.GetValueOrDefault(c.Id);
            var spent = recurringCents + expensesCents;
            long? budget = budgets.TryGetValue(c.Id, out var b) ? b : null;
            return new BudgetRowDto(
                c.Id,
                c.Name,
                c.Color,
                c.Icon,
                budget,
                recurringCents,
                expensesCents,
                spent,
                budget is { } amount ? amount - spent : null,
                Percent(budget, spent));
        }).ToList();
    }

    /// <summary>Whole-number percentage of the budget spent; null without a budget. A zero budget is 0 % when nothing was spent, else 100 %.</summary>
    public static int? Percent(long? budgetCents, long spentCents)
    {
        if (budgetCents is not { } budget)
        {
            return null;
        }

        if (budget <= 0)
        {
            return spentCents > 0 ? 100 : 0;
        }

        return (int)Math.Round(spentCents * 100m / budget, 0, MidpointRounding.AwayFromZero);
    }

    private static Dictionary<Guid, long> Sum(IEnumerable<CategoryAmount> amounts) =>
        amounts.Where(a => a.CategoryId is not null)
            .GroupBy(a => a.CategoryId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Cents));
}
