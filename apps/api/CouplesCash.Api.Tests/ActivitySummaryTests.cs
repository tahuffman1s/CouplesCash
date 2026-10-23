using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Services;

namespace CouplesCash.Api.Tests;

public class ActivitySummaryTests
{
    [Theory]
    [InlineData(185_000, "USD", "$1,850.00")]
    [InlineData(1_250, "EUR", "€12.50")]
    [InlineData(-300, "GBP", "-£3.00")]
    [InlineData(100_000_00, "CAD", "CA$100,000.00")]
    [InlineData(5, "AUD", "A$0.05")]
    [InlineData(123_456, "CHF", "CHF 1,234.56")]
    [InlineData(0, "usd", "$0.00")]
    public void Money_formats_with_symbol_or_iso_code(long cents, string currency, string expected)
    {
        Assert.Equal(expected, Money.Format(cents, currency));
    }

    [Fact]
    public void Occurrence_paid_matches_the_contract_example()
    {
        Assert.Equal("marked Rent paid · $1,850.00", ActivitySummaries.OccurrencePaid("Rent", 185_000, "USD"));
        Assert.Equal("Rent auto-paid · $1,850.00", ActivitySummaries.OccurrenceAutopaid("Rent", 185_000, "USD"));
        Assert.Equal("marked Rent unpaid", ActivitySummaries.OccurrenceUnpaid("Rent"));
    }

    [Fact]
    public void Recurring_created_names_kind_amount_and_frequency()
    {
        var item = new RecurringItemRow { Name = "Netflix", Kind = Kind.Subscription, AmountCents = 1_599, Frequency = Frequency.Monthly };

        Assert.Equal("added subscription Netflix · $15.99 monthly", ActivitySummaries.RecurringCreated(item, "USD"));
    }

    [Fact]
    public void Goal_contribution_wording_depends_on_sign()
    {
        var goal = new GoalRow { Name = "Vacation" };

        Assert.Equal("added $200.00 to Vacation", ActivitySummaries.GoalContributed(goal, 20_000, "USD"));
        Assert.Equal("took $50.00 from Vacation", ActivitySummaries.GoalContributed(goal, -5_000, "USD"));
    }

    [Fact]
    public void Todo_created_appends_amount_only_when_present()
    {
        Assert.Equal("added to-pay Call insurance", ActivitySummaries.TodoCreated(new TodoRow { Title = "Call insurance" }, "USD"));
        Assert.Equal("added to-pay Transfer · $200.00", ActivitySummaries.TodoCreated(new TodoRow { Title = "Transfer", AmountCents = 20_000 }, "USD"));
    }

    [Fact]
    public void Settings_summary_lists_changed_fields()
    {
        Assert.Equal("updated household settings", ActivitySummaries.CoupleSettings([]));
        Assert.Equal("updated household settings (currency, time zone)", ActivitySummaries.CoupleSettings(["currency", "time zone"]));
        Assert.Equal("renamed the household to Us", ActivitySummaries.CoupleRenamed("Us"));
    }

    [Fact]
    public void Plain_strips_stray_bold_markers_for_push_bodies()
    {
        Assert.Equal("marked Rent paid · $1,850.00", ActivityService.Plain("marked **Rent** paid · $1,850.00"));
    }

    [Fact]
    public void Deep_links_point_at_the_entity()
    {
        var id = Guid.Parse("00000000-0000-7000-8000-000000000001");

        Assert.Equal($"/goals/{id}", ActivityService.DeepLink("goal", id, "goal.updated"));
        Assert.Equal("/goals", ActivityService.DeepLink("goal", id, "goal.deleted"));
        Assert.Equal("/calendar", ActivityService.DeepLink("occurrence", id, "occurrence.paid"));
        Assert.Equal("/activity", ActivityService.DeepLink("mystery", null, "x.y"));
    }

    [Fact]
    public void Data_serializes_camel_case_and_omits_nulls()
    {
        var json = ActivityService.SerializeData(new { ItemId = 1, DueDate = new DateOnly(2026, 2, 28), Note = (string?)null });

        Assert.Equal("{\"itemId\":1,\"dueDate\":\"2026-02-28\"}", json);
        Assert.Equal("{}", ActivityService.SerializeData(null));
    }
}
