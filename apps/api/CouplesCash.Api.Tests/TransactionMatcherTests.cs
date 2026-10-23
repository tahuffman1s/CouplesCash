using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class TransactionMatcherTests
{
    private static readonly Guid Rent = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid Netflix = Guid.Parse("00000000-0000-7000-8000-000000000002");
    private static readonly Guid Salary = Guid.Parse("00000000-0000-7000-8000-000000000003");
    private static readonly Guid Groceries = Guid.Parse("00000000-0000-7000-8000-000000000020");
    private static readonly Guid Subscriptions = Guid.Parse("00000000-0000-7000-8000-000000000021");
    private static readonly Guid Fun = Guid.Parse("00000000-0000-7000-8000-000000000022");
    private static readonly Guid Income = Guid.Parse("00000000-0000-7000-8000-000000000023");
    private static readonly Guid Checking = Guid.Parse("00000000-0000-7000-8000-000000000030");
    private static readonly Guid Savings = Guid.Parse("00000000-0000-7000-8000-000000000031");
    private static readonly Guid Unmapped = Guid.Parse("00000000-0000-7000-8000-000000000032");

    private static readonly Dictionary<string, Guid> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Groceries"] = Groceries,
        ["Subscriptions"] = Subscriptions,
        ["Fun"] = Fun,
        ["Income"] = Income,
    };

    private static readonly Dictionary<string, PayeeRule> NoRules = new(StringComparer.Ordinal);

    private static readonly MatchableOccurrence[] Unpaid =
    [
        new(Rent, "Rent", Kind.Bill, 185000, new DateOnly(2026, 10, 1)),
        new(Netflix, "Netflix", Kind.Subscription, 1549, new DateOnly(2026, 10, 10)),
        new(Salary, "Salary", Kind.Income, 320000, new DateOnly(2026, 10, 15)),
    ];

    private static MatchTransaction Tx(long cents, string description, int day, string? payee = null, Guid? account = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), account ?? Checking, new DateOnly(2026, 10, day), cents, description, payee);

    private static Dictionary<string, PayeeRule> Rules(params PayeeRule[] rules) => rules.ToDictionary(r => r.PayeeNorm, StringComparer.Ordinal);

    // ---------------------------------------------------------------------------------------------
    // Normalisation / tolerance
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "KROGER #123", "kroger")]
    [InlineData(null, "POS DEBIT CARD 1234 KROGER #123 10/02", "kroger")]
    [InlineData(null, "NETFLIX.COM", "netflix com")]
    [InlineData(null, "RENT PAYMENT", "rent payment")]
    [InlineData("Kroger", "KROGER #123", "kroger")]
    [InlineData(null, "SQ *BLUE BOTTLE COFFEE", "blue bottle coffee")]
    [InlineData(null, "1234 5678", "1234 5678")] // only digits: fall back to the plain normalisation
    public void NormalisePayee_strips_noise_digits_and_prefers_the_payee(string? payee, string description, string expected)
    {
        Assert.Equal(expected, TransactionMatcher.NormalisePayee(payee, description));
    }

    [Theory]
    [InlineData(1000, 100)]      // $10 → $1 floor
    [InlineData(5000, 100)]      // $50 → 2 % = $1
    [InlineData(185000, 3700)]   // $1,850 → 2 % = $37
    [InlineData(-185000, 3700)]
    public void Tolerance_is_max_of_two_percent_and_one_dollar(long expected, long tolerance)
    {
        Assert.Equal(tolerance, TransactionMatcher.Tolerance(expected));
    }

    [Theory]
    [InlineData(185000, 185000, true)]
    [InlineData(185000, 188700, true)]   // +$37 exactly
    [InlineData(185000, 188701, false)]  // one cent over
    [InlineData(1549, 1649, true)]       // $1 floor on a small amount
    [InlineData(1549, 1650, false)]
    public void AmountMatches_respects_the_tolerance(long expected, long actual, bool matches)
    {
        Assert.Equal(matches, TransactionMatcher.AmountMatches(expected, actual));
    }

    // ---------------------------------------------------------------------------------------------
    // Recurring match
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Recurring_match_within_window_and_tolerance_scores_90()
    {
        var suggestion = TransactionMatcher.Suggest(Tx(-185000, "RENT PAYMENT", day: 3), Unpaid, NoRules, Categories);

        Assert.Equal(SuggestionKind.Occurrence, suggestion.Kind);
        Assert.Equal(Rent, suggestion.ItemId);
        Assert.Equal(new DateOnly(2026, 10, 1), suggestion.DueDate);
        Assert.Equal(90, suggestion.Confidence);
    }

    [Theory]
    [InlineData(6, true)]   // Oct 6 for an Oct 1 due date: 5 days → in
    [InlineData(7, false)]  // 6 days → out
    public void Recurring_match_date_window_is_five_days(int day, bool matched)
    {
        var suggestion = TransactionMatcher.Suggest(Tx(-185000, "RENT PAYMENT", day), Unpaid, NoRules, Categories);

        Assert.Equal(matched, suggestion.Kind == SuggestionKind.Occurrence);
    }

    [Fact]
    public void Recurring_match_rejects_amounts_outside_two_percent()
    {
        var suggestion = TransactionMatcher.Suggest(Tx(-190000, "RENT PAYMENT", day: 1), Unpaid, NoRules, Categories);

        Assert.Equal(SuggestionKind.None, suggestion.Kind);
        Assert.Equal(0, suggestion.Confidence);
    }

    [Fact]
    public void Recurring_match_requires_sign_to_agree_with_kind()
    {
        var refund = TransactionMatcher.Suggest(Tx(+185000, "RENT REFUND", day: 1), Unpaid, NoRules, Categories);
        var deduction = TransactionMatcher.Suggest(Tx(-320000, "ACME PAYROLL", day: 15), Unpaid, NoRules, Categories);

        Assert.NotEqual(SuggestionKind.Occurrence, refund.Kind);
        Assert.NotEqual(SuggestionKind.Occurrence, deduction.Kind);
    }

    [Fact]
    public void Income_matches_inflows()
    {
        var suggestion = TransactionMatcher.Suggest(Tx(+320000, "ACME PAYROLL", day: 14), Unpaid, NoRules, Categories);

        Assert.Equal(SuggestionKind.Occurrence, suggestion.Kind);
        Assert.Equal(Salary, suggestion.ItemId);
    }

    [Fact]
    public void Learned_item_rule_adds_five()
    {
        var rules = Rules(new PayeeRule("rent payment", null, Rent, 3));

        var suggestion = TransactionMatcher.Suggest(Tx(-185000, "RENT PAYMENT", day: 1), Unpaid, rules, Categories);

        Assert.Equal(95, suggestion.Confidence);
        Assert.True(TransactionMatcher.RuleMatchesItem(Tx(-185000, "RENT PAYMENT", day: 1), Rent, rules));
        Assert.False(TransactionMatcher.RuleMatchesItem(Tx(-185000, "RENT PAYMENT", day: 1), Netflix, rules));
    }

    [Fact]
    public void Closest_due_date_wins_when_several_occurrences_fit()
    {
        var weekly = new MatchableOccurrence[]
        {
            new(Netflix, "Gym", Kind.Subscription, 2999, new DateOnly(2026, 10, 5)),
            new(Netflix, "Gym", Kind.Subscription, 2999, new DateOnly(2026, 10, 12)),
        };

        var suggestion = TransactionMatcher.Suggest(Tx(-2999, "GYM", day: 10), weekly, NoRules, Categories);

        Assert.Equal(new DateOnly(2026, 10, 12), suggestion.DueDate);
    }

    // ---------------------------------------------------------------------------------------------
    // Rules and keywords
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Payee_rule_beats_keyword_table()
    {
        var rules = Rules(new PayeeRule("kroger", Fun, null, 4)); // the user files Kroger under Fun, whatever the keyword table says

        var suggestion = TransactionMatcher.Suggest(Tx(-1249, "KROGER #123", day: 2), Unpaid, rules, Categories);

        Assert.Equal(SuggestionKind.Category, suggestion.Kind);
        Assert.Equal(Fun, suggestion.CategoryId);
        Assert.Equal(74, suggestion.Confidence);
    }

    [Theory]
    [InlineData(1, 71)]
    [InlineData(20, 90)]
    [InlineData(99, 90)]
    public void Payee_rule_confidence_is_70_plus_hits_capped_at_20(int hits, int confidence)
    {
        var rules = Rules(new PayeeRule("kroger", Groceries, null, hits));

        Assert.Equal(confidence, TransactionMatcher.Suggest(Tx(-1249, "KROGER #123", day: 2), Unpaid, rules, Categories).Confidence);
    }

    [Theory]
    [InlineData("KROGER #123", "Groceries")]
    [InlineData("TRADER JOE'S #55", "Groceries")]
    [InlineData("NETFLIX.COM", "Subscriptions")]
    [InlineData("SPOTIFY USA", "Subscriptions")]
    [InlineData("SHELL OIL 12345", "Transportation")]
    [InlineData("UBER EATS", "Fun")]
    [InlineData("UBER *TRIP", "Transportation")]
    [InlineData("CITY WATER UTILITY", "Utilities")]
    [InlineData("GEICO INSURANCE", "Insurance")]
    [InlineData("CVS PHARMACY", "Health")]
    [InlineData("SQ *BLUE BOTTLE COFFEE", "Fun")]
    [InlineData("SHELLFISH MARKET", null)] // "shell" is a whole-token match only
    [InlineData("MYSTERY VENDOR", null)]
    public void Keyword_table_maps_common_merchants_for_outflows(string description, string? category)
    {
        Assert.Equal(category, TransactionMatcher.KeywordCategory(description, isOutflow: true));
    }

    [Fact]
    public void Income_keywords_apply_to_inflows_only()
    {
        Assert.Equal("Income", TransactionMatcher.KeywordCategory("ACME CORP PAYROLL DIRECT DEP", isOutflow: false));
        Assert.Null(TransactionMatcher.KeywordCategory("ACME CORP PAYROLL DIRECT DEP", isOutflow: true));
        Assert.Null(TransactionMatcher.KeywordCategory("KROGER REFUND", isOutflow: false));
    }

    [Fact]
    public void Keyword_match_scores_40_and_needs_the_household_category()
    {
        var suggestion = TransactionMatcher.Suggest(Tx(-1549, "NETFLIX.COM", day: 20), Unpaid, NoRules, Categories);
        Assert.Equal(SuggestionKind.Category, suggestion.Kind);
        Assert.Equal(Subscriptions, suggestion.CategoryId);
        Assert.Equal(40, suggestion.Confidence);

        var withoutCategory = TransactionMatcher.Suggest(Tx(-1549, "NETFLIX.COM", day: 20), Unpaid, NoRules, new Dictionary<string, Guid>());
        Assert.Equal(Suggestion.None, withoutCategory);
    }

    [Fact]
    public void Unknown_payee_has_no_suggestion()
    {
        var suggestion = TransactionMatcher.Suggest(Tx(-4321, "ZZZ UNKNOWN LLC", day: 20), Unpaid, NoRules, Categories);

        Assert.Equal(Suggestion.None, suggestion);
    }

    // ---------------------------------------------------------------------------------------------
    // Transfers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Transfer_pairs_opposite_amounts_between_mapped_accounts_within_three_days()
    {
        var outgoing = Tx(-50000, "TRANSFER TO SAVINGS", day: 5, account: Checking);
        var incoming = Tx(+50000, "TRANSFER FROM CHECKING", day: 8, account: Savings);

        var transfers = TransactionMatcher.DetectTransfers([outgoing, incoming], new HashSet<Guid> { Checking, Savings });

        Assert.Equal(new HashSet<Guid> { outgoing.Id, incoming.Id }, transfers);
    }

    [Fact]
    public void Transfer_is_not_detected_across_four_days_same_account_or_unmapped_accounts()
    {
        var mapped = new HashSet<Guid> { Checking, Savings };
        var a = Tx(-50000, "X", day: 5, account: Checking);

        Assert.Empty(TransactionMatcher.DetectTransfers([a, Tx(+50000, "Y", day: 9, account: Savings)], mapped));
        Assert.Empty(TransactionMatcher.DetectTransfers([a, Tx(+50000, "Y", day: 6, account: Checking)], mapped));
        Assert.Empty(TransactionMatcher.DetectTransfers([a, Tx(+50000, "Y", day: 6, account: Unmapped)], mapped));
        Assert.Empty(TransactionMatcher.DetectTransfers([a, Tx(-50000, "Y", day: 6, account: Savings)], mapped));
    }

    [Fact]
    public void SuggestAll_marks_transfer_legs_with_confidence_80_and_each_leg_pairs_once()
    {
        var mapped = new HashSet<Guid> { Checking, Savings };
        var outgoing = Tx(-50000, "TRANSFER", day: 5, account: Checking);
        var incoming = Tx(+50000, "TRANSFER", day: 5, account: Savings);
        var secondIncoming = Tx(+50000, "TRANSFER", day: 6, account: Savings);

        var result = TransactionMatcher.SuggestAll([outgoing, incoming, secondIncoming], Unpaid, [], Categories, mapped);

        Assert.Equal(Suggestion.Transfer, result[outgoing.Id]);
        Assert.Equal(80, result[incoming.Id].Confidence);
        Assert.Equal(SuggestionKind.None, result[secondIncoming.Id].Kind);
    }

    [Fact]
    public void Recurring_match_beats_transfer_detection()
    {
        var mapped = new HashSet<Guid> { Checking, Savings };
        var payment = Tx(-185000, "RENT", day: 1, account: Checking);
        var mirror = Tx(+185000, "RENT", day: 1, account: Savings);

        var result = TransactionMatcher.SuggestAll([payment, mirror], Unpaid, [], Categories, mapped);

        Assert.Equal(SuggestionKind.Occurrence, result[payment.Id].Kind);
        Assert.Equal(SuggestionKind.Transfer, result[mirror.Id].Kind); // the other leg still looks like a transfer
    }

    [Fact]
    public void SuggestAll_hands_each_occurrence_out_once()
    {
        var first = Tx(-185000, "RENT PAYMENT", day: 1, id: Guid.Parse("00000000-0000-7000-8000-0000000000a1"));
        var second = Tx(-185000, "RENT PAYMENT", day: 2, id: Guid.Parse("00000000-0000-7000-8000-0000000000a2"));

        var result = TransactionMatcher.SuggestAll([first, second], Unpaid, [], Categories, new HashSet<Guid>());

        Assert.Equal(Rent, result[first.Id].ItemId);
        Assert.Equal(SuggestionKind.None, result[second.Id].Kind);
    }

    [Fact]
    public void SuggestAll_uses_other_pending_rows_as_the_other_transfer_leg()
    {
        var mapped = new HashSet<Guid> { Checking, Savings };
        var incoming = Tx(+50000, "TRANSFER", day: 5, account: Savings);
        var alreadyPending = Tx(-50000, "TRANSFER", day: 4, account: Checking);

        var result = TransactionMatcher.SuggestAll([incoming], Unpaid, [], Categories, mapped, otherPending: [alreadyPending]);

        Assert.Equal(SuggestionKind.Transfer, result[incoming.Id].Kind);
        Assert.False(result.ContainsKey(alreadyPending.Id));
    }
}
