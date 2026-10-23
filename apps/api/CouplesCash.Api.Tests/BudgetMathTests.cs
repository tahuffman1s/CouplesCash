using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class BudgetMathTests
{
    private static readonly Guid Groceries = Guid.Parse("00000000-0000-7000-8000-000000000010");
    private static readonly Guid Housing = Guid.Parse("00000000-0000-7000-8000-000000000011");
    private static readonly Guid Fun = Guid.Parse("00000000-0000-7000-8000-000000000012");

    private static readonly BudgetCategory[] Categories =
    [
        new(Groceries, "Groceries", "#22c55e", "shopping-cart"),
        new(Housing, "Housing", "#f97316", "home"),
        new(Fun, "Fun", "#f43f5e", "party-popper"),
    ];

    [Fact]
    public void Rows_cover_every_category_and_sum_recurring_plus_expenses()
    {
        var budgets = new Dictionary<Guid, long> { [Groceries] = 60_000, [Housing] = 200_000 };
        var recurring = new[] { new CategoryAmount(Housing, 185_000), new CategoryAmount(Groceries, 10_000), new CategoryAmount(null, 999) };
        var expenses = new[] { new CategoryAmount(Groceries, 42_10), new CategoryAmount(Groceries, 12_000), new CategoryAmount(Fun, 5_000), new CategoryAmount(null, 1) };

        var rows = BudgetMath.Rows(Categories, budgets, recurring, expenses);

        Assert.Equal(3, rows.Count);

        var groceries = rows.Single(r => r.CategoryId == Groceries);
        Assert.Equal(60_000, groceries.BudgetCents);
        Assert.Equal(10_000, groceries.RecurringCents);
        Assert.Equal(16_210, groceries.ExpensesCents);
        Assert.Equal(26_210, groceries.SpentCents);
        Assert.Equal(33_790, groceries.RemainingCents);
        Assert.Equal(44, groceries.Percent);

        var housing = rows.Single(r => r.CategoryId == Housing);
        Assert.Equal(185_000, housing.SpentCents);
        Assert.Equal(15_000, housing.RemainingCents);
        Assert.Equal(93, housing.Percent); // 92.5 rounds away from zero

        var fun = rows.Single(r => r.CategoryId == Fun);
        Assert.Null(fun.BudgetCents);
        Assert.Equal(5_000, fun.SpentCents);
        Assert.Null(fun.RemainingCents);
        Assert.Null(fun.Percent);
    }

    [Fact]
    public void Over_budget_percent_is_not_capped_and_remaining_goes_negative()
    {
        var rows = BudgetMath.Rows(Categories, new Dictionary<Guid, long> { [Fun] = 10_000 }, [], [new CategoryAmount(Fun, 25_000)]);

        var fun = rows.Single(r => r.CategoryId == Fun);
        Assert.Equal(250, fun.Percent);
        Assert.Equal(-15_000, fun.RemainingCents);
    }

    [Theory]
    [InlineData(null, 500, null)]
    [InlineData(0L, 0, 0)]
    [InlineData(0L, 1, 100)]
    [InlineData(1000L, 0, 0)]
    [InlineData(1000L, 1000, 100)]
    [InlineData(1000L, 333, 33)]
    [InlineData(1000L, 335, 34)]
    public void Percent_rounds_to_whole_numbers(long? budget, long spent, int? expected)
    {
        Assert.Equal(expected, BudgetMath.Percent(budget, spent));
    }

    [Fact]
    public void Active_in_month_requires_overlap()
    {
        var march = new DateOnly(2026, 3, 1);

        Assert.True(BudgetMath.IsActiveInMonth(new DateOnly(2025, 1, 15), null, march));
        Assert.True(BudgetMath.IsActiveInMonth(new DateOnly(2026, 3, 31), null, march));
        Assert.False(BudgetMath.IsActiveInMonth(new DateOnly(2026, 4, 1), null, march));
        Assert.True(BudgetMath.IsActiveInMonth(new DateOnly(2025, 1, 1), new DateOnly(2026, 3, 1), march));
        Assert.False(BudgetMath.IsActiveInMonth(new DateOnly(2025, 1, 1), new DateOnly(2026, 2, 28), march));
    }
}
