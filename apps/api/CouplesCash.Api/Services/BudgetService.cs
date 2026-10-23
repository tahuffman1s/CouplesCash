using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Services;

/// <summary>Budget rows for a month: every category, budgeted or not (see <see cref="BudgetMath"/>).</summary>
public sealed class BudgetService(BudgetRepository budgets)
{
    public async Task<IReadOnlyList<BudgetRowDto>> RowsAsync(Guid householdId, MonthData month, CancellationToken ct)
    {
        var budgetRows = await budgets.ListAsync(householdId, ct);
        return Rows(month, budgetRows);
    }

    public static IReadOnlyList<BudgetRowDto> Rows(MonthData month, IReadOnlyList<BudgetRow> budgetRows) =>
        BudgetMath.Rows(
            month.Categories.Select(c => new BudgetCategory(c.Id, c.Name, c.Color, c.Icon)),
            budgetRows.ToDictionary(b => b.CategoryId, b => b.AmountCents),
            month.ActiveItems.Where(i => i.Kind.IsOutflow()).Select(i => new CategoryAmount(i.CategoryId, RecurrenceEngine.MonthlyEquivalent(i.AmountCents, i.Frequency))),
            month.Expenses.Select(e => new CategoryAmount(e.CategoryId, e.AmountCents)));

    /// <summary>Totals over budgeted categories only, so the overall bar compares like with like.</summary>
    public static (long Budget, long Spent) Totals(IEnumerable<BudgetRowDto> rows)
    {
        long budget = 0, spent = 0;
        foreach (var row in rows.Where(r => r.BudgetCents is not null))
        {
            budget += row.BudgetCents!.Value;
            spent += row.SpentCents;
        }

        return (budget, spent);
    }
}
