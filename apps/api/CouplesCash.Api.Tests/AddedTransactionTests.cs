using CouplesCash.Api.Contracts;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Validation;
using FluentValidation;

namespace CouplesCash.Api.Tests;

/// <summary>Transactions added by hand: which bank row is the bank's own copy (<see cref="ManualMatcher"/>) and the request rules.</summary>
public class AddedTransactionTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:000000000000}");

    private static ManualOpen Added(int id, long cents, string name, int day = 0) => new(Id(id), Today.AddDays(day), cents, name, DateTime.UnixEpoch.AddSeconds(id));

    private static ManualCandidate Bank(int id, long cents, string description, int day = 0) => new(Id(id), Today.AddDays(day), cents, description, null);

    [Fact]
    public void The_same_amount_a_few_days_later_is_the_banks_copy_whatever_its_name()
    {
        Assert.Equal([(Id(1), Id(10))], ManualMatcher.Match([Added(1, -2_500, "groceries")], [Bank(10, -2_500, "WAL-MART #1234", day: 2)]));
        Assert.Equal([(Id(1), Id(10))], ManualMatcher.Match([Added(1, -2_500, "groceries")], [Bank(10, -2_560, "POS 4471", day: 1)])); // ≤ $1
        Assert.Empty(ManualMatcher.Match([Added(1, -2_500, "groceries")], [Bank(10, -2_700, "POS 4471")]));                            // $2 off, other name
    }

    [Fact]
    public void A_tip_or_a_fuel_hold_matches_when_the_names_agree()
    {
        Assert.Equal([(Id(1), Id(10))], ManualMatcher.Match([Added(1, -4_000, "The Diner")], [Bank(10, -4_800, "THE DINER 0042", day: 1)]));
        Assert.Empty(ManualMatcher.Match([Added(1, -4_000, "The Diner")], [Bank(10, -6_000, "THE DINER 0042")]));                      // past 25 %
    }

    [Theory]
    [InlineData(-3, true)]
    [InlineData(7, true)]
    [InlineData(-4, false)]
    [InlineData(8, false)]
    public void The_bank_row_is_dated_three_days_before_to_a_week_after(int day, bool matches) =>
        Assert.Equal(matches, ManualMatcher.Match([Added(1, -650, "coffee")], [Bank(10, -650, "STARBUCKS", day)]).Count == 1);

    [Fact]
    public void Money_in_never_matches_money_out_and_each_row_is_used_once()
    {
        Assert.Empty(ManualMatcher.Match([Added(1, -2_000, "refund")], [Bank(10, 2_000, "REFUND")]));

        // two coffees added, one bank coffee: the closer date wins, the other stays open
        var pairs = ManualMatcher.Match([Added(1, -650, "coffee", day: -2), Added(2, -650, "coffee")], [Bank(10, -650, "STARBUCKS")]);
        Assert.Equal([(Id(2), Id(10))], pairs);

        // two of each: both pair up
        Assert.Equal(2, ManualMatcher.Match([Added(1, -650, "coffee"), Added(2, -650, "coffee")], [Bank(10, -650, "SBUX"), Bank(11, -650, "SBUX")]).Count);
    }

    [Fact]
    public void Requests_name_an_account_an_amount_and_a_recent_date()
    {
        var validator = new AddTransactionRequestValidator();
        string[] Fields(AddTransactionRequest r) => ValidationFilter<object>.ToErrors(validator.Validate(r)).Keys.Order().ToArray();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var ok = new AddTransactionRequest(Id(1), -650, "Coffee", today, null);

        Assert.Empty(Fields(ok));
        Assert.Empty(Fields(ok with { AmountCents = 2_000, Force = true }));
        Assert.Equal(["amountCents"], Fields(ok with { AmountCents = 0 }));
        Assert.Equal(["amountCents"], Fields(ok with { AmountCents = -100_000_001 }));
        Assert.Equal(["description"], Fields(ok with { Description = "" }));
        Assert.Equal(["description"], Fields(ok with { Description = new string('x', 121) }));
        Assert.Equal(["postedOn"], Fields(ok with { PostedOn = today.AddDays(8) }));
        Assert.Equal(["postedOn"], Fields(ok with { PostedOn = today.AddDays(-61) }));
        Assert.Equal(["accountId"], Fields(ok with { AccountId = Guid.Empty }));
    }
}
