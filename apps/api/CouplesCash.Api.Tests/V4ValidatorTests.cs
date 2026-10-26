using CouplesCash.Api.Contracts;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Validation;
using FluentValidation;

namespace CouplesCash.Api.Tests;

public class V4ValidatorTests
{
    private static string[] Fields<T>(AbstractValidator<T> validator, T value) =>
        ValidationFilter<object>.ToErrors(validator.Validate(value)).Keys.Order().ToArray();

    private static HouseholdSettingsRequest Settings(Optional<long?> weekly) =>
        new(default, default, default, default, default, default, default, weekly, default);

    private static RecurringRequest OneOff(string? hint) =>
        new("Oil change", Kind.Bill, 6000, null, null, Frequency.Once, new DateOnly(2026, 10, 22), null, null, null, null, hint);

    [Fact]
    public void Weekly_spend_target_must_not_be_negative()
    {
        Assert.Equal(["weeklySpendCents"], Fields(new HouseholdSettingsRequestValidator(), Settings(Optional<long?>.Of(-1))));
        Assert.Equal(["weeklySpendCents"], Fields(new HouseholdSettingsRequestValidator(), Settings(Optional<long?>.Of(Rules.MaxCents + 1))));
    }

    [Fact]
    public void Weekly_spend_target_accepts_zero_a_value_null_to_clear_and_absence()
    {
        Assert.Empty(Fields(new HouseholdSettingsRequestValidator(), Settings(Optional<long?>.Of(0))));
        Assert.Empty(Fields(new HouseholdSettingsRequestValidator(), Settings(Optional<long?>.Of(20_000))));
        Assert.Empty(Fields(new HouseholdSettingsRequestValidator(), Settings(Optional<long?>.Of(null))));
        Assert.Empty(Fields(new HouseholdSettingsRequestValidator(), Settings(Optional<long?>.Missing)));
    }

    [Fact]
    public void Payee_hint_is_optional_and_at_most_80_characters()
    {
        Assert.Empty(Fields(new RecurringRequestValidator(), OneOff(null)));
        Assert.Empty(Fields(new RecurringRequestValidator(), OneOff("JIFFY LUBE")));
        Assert.Empty(Fields(new RecurringRequestValidator(), OneOff(new string('x', RecurringRequestValidator.MaxPayeeHintLength))));
        Assert.Equal(["payeeHint"], Fields(new RecurringRequestValidator(), OneOff(new string('x', RecurringRequestValidator.MaxPayeeHintLength + 1))));
    }

    [Theory]
    [InlineData(null, true, BudgetPeriod.Monthly)]
    [InlineData("", true, BudgetPeriod.Monthly)]
    [InlineData("monthly", true, BudgetPeriod.Monthly)]
    [InlineData("weekly", true, BudgetPeriod.Weekly)]
    [InlineData("Weekly", true, BudgetPeriod.Weekly)]
    [InlineData("daily", false, BudgetPeriod.Monthly)]
    [InlineData("1", false, BudgetPeriod.Monthly)]
    public void Budget_period_query_is_monthly_or_weekly(string? period, bool ok, BudgetPeriod expected)
    {
        Assert.Equal(ok, BudgetEndpoints.TryParsePeriod(period, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void Push_prefs_default_the_week_summary_toggle_on_for_older_clients()
    {
        var request = new PushPrefsRequest(true, [1], 9, true);

        Assert.True(request.WeekSummaryEnabled);
        Assert.True(new PushPrefsRequestValidator().Validate(request).IsValid);
        Assert.False(new PushPrefsRequest(true, [1], 9, true, false).WeekSummaryEnabled);
    }
}
