using CouplesCash.Api.Contracts;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using FluentValidation;

namespace CouplesCash.Api.Tests;

/// <summary>v5: transfer keywords / peers / learned rules, candidate ranking, transfer analysis, bill payment dates, spendable, validators.</summary>
public class V5Tests
{
    private static readonly Guid Checking = Guid.Parse("00000000-0000-7000-8000-000000000030");
    private static readonly Guid Savings = Guid.Parse("00000000-0000-7000-8000-000000000031");
    private static readonly Guid Brokerage = Guid.Parse("00000000-0000-7000-8000-000000000033");
    private static readonly Guid Groceries = Guid.Parse("00000000-0000-7000-8000-000000000020");
    private static readonly Dictionary<string, Guid> Categories = new(StringComparer.OrdinalIgnoreCase) { ["Groceries"] = Groceries };

    private static MatchTransaction Tx(long cents, string description, int day, Guid? account = null, string? payee = null) =>
        new(Guid.NewGuid(), account ?? Checking, new DateOnly(2026, 10, day), cents, description, payee);

    private static string[] Fields<T>(AbstractValidator<T> validator, T value) =>
        ValidationFilter<object>.ToErrors(validator.Validate(value)).Keys.Order().ToArray();

    // ---------------------------------------------------------------------------------------------
    // Transfer keywords and learned transfer payees
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ONLINE TRANSFER TO SAV 1234", true)]
    [InlineData("TRANSFER TO BROKERAGE", true)]
    [InlineData("Transfer from Checking", true)]
    [InlineData("XFER 991 SAVINGS", true)]
    [InlineData("ZELLE TRANSFER TO JOHN", false)]
    [InlineData("VENMO TRANSFER", false)]
    [InlineData("CASH APP*TRANSFER", false)]
    [InlineData("TRANSFERWISE FEE", false)]
    [InlineData("PAYMENT THANK YOU", false)] // money out: only a card payment received counts
    [InlineData("KROGER #123", false)]
    public void Transfer_keywords_for_money_out(string description, bool expected) =>
        Assert.Equal(expected, TransactionMatcher.IsTransferKeyword(description, null, isOutflow: true));

    [Theory]
    [InlineData("PAYMENT THANK YOU", true)]
    [InlineData("AUTOPAY PAYMENT - THANK YOU", true)]
    [InlineData("ONLINE PMT CKF123", true)]
    [InlineData("ACME CORP PAYROLL", false)]
    public void Transfer_keywords_for_money_in(string description, bool expected) =>
        Assert.Equal(expected, TransactionMatcher.IsTransferKeyword(description, null, isOutflow: false));

    [Fact]
    public void One_sided_transfer_keyword_is_suggested_at_60_below_the_bulk_threshold()
    {
        var tx = Tx(-100000, "TRANSFER TO BROKERAGE", 5);

        var suggestion = TransactionMatcher.SuggestAll([tx], [], [], Categories, new HashSet<Guid> { Checking })[tx.Id];

        Assert.Equal(SuggestionKind.Transfer, suggestion.Kind);
        Assert.Equal(60, suggestion.Confidence);
        Assert.True(suggestion.Confidence < TransactionMatcher.BulkConfidence);
    }

    [Fact]
    public void Learned_transfer_payee_beats_a_learned_category_and_scores_85()
    {
        var tx = Tx(-100000, "TRANSFER TO BROKERAGE", 5);
        var rule = new PayeeRule(TransactionMatcher.NormalisePayee(null, tx.Description), Groceries, null, 3, IsTransfer: true);

        var suggestion = TransactionMatcher.SuggestAll([tx], [], [rule], Categories, new HashSet<Guid> { Checking })[tx.Id];

        Assert.Equal(SuggestionKind.Transfer, suggestion.Kind);
        Assert.Equal(TransactionMatcher.TransferRuleConfidence, suggestion.Confidence);
    }

    [Fact]
    public void Learned_category_beats_a_transfer_keyword()
    {
        var tx = Tx(-5000, "XFER FEE STORE", 5);
        var rule = new PayeeRule(TransactionMatcher.NormalisePayee(null, tx.Description), Groceries, null, 1);

        var suggestion = TransactionMatcher.SuggestAll([tx], [], [rule], Categories, new HashSet<Guid> { Checking })[tx.Id];

        Assert.Equal(SuggestionKind.Category, suggestion.Kind);
        Assert.Equal(71, suggestion.Confidence);
    }

    [Fact]
    public void Detected_pairs_are_reported_with_both_legs()
    {
        var mapped = new HashSet<Guid> { Checking, Savings };
        var outflow = Tx(-50000, "ONLINE TRANSFER TO SAV", 5, Checking);
        var inflow = Tx(50000, "ONLINE TRANSFER FROM CHK", 6, Savings);

        var pair = Assert.Single(TransactionMatcher.DetectTransferPairs([outflow, inflow, Tx(-1200, "KROGER", 5)], mapped));

        Assert.Equal(new HashSet<Guid> { outflow.Id, inflow.Id }, new HashSet<Guid> { pair.A, pair.B });
    }

    [Fact]
    public void Manual_transfer_peer_rules()
    {
        var outflow = Tx(-100000, "TRANSFER", 10, Checking);

        Assert.True(TransactionMatcher.IsTransferPeer(outflow, Tx(100000, "DEPOSIT", 15, Savings)));
        Assert.True(TransactionMatcher.IsTransferPeer(outflow, Tx(99100, "DEPOSIT", 5, Brokerage))); // within 1 %
        Assert.False(TransactionMatcher.IsTransferPeer(outflow, Tx(98900, "DEPOSIT", 10, Savings))); // 1.1 %
        Assert.False(TransactionMatcher.IsTransferPeer(outflow, Tx(100000, "DEPOSIT", 16, Savings))); // 6 days
        Assert.False(TransactionMatcher.IsTransferPeer(outflow, Tx(-100000, "TRANSFER", 10, Savings))); // same sign
        Assert.False(TransactionMatcher.IsTransferPeer(outflow, Tx(100000, "REFUND", 10, Checking))); // same account
        Assert.False(TransactionMatcher.IsTransferPeer(outflow, outflow));
    }

    // ---------------------------------------------------------------------------------------------
    // Candidate ranking
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Candidate_score_is_amount_date_and_rule()
    {
        var due = new DateOnly(2026, 10, 10);

        Assert.Equal(80, TransactionMatcher.CandidateScore(Kind.Bill, 185000, due, Tx(-185000, "RENT", 10), 30, ruleHit: false));
        Assert.Equal(100, TransactionMatcher.CandidateScore(Kind.Bill, 185000, due, Tx(-185000, "RENT", 10), 30, ruleHit: true));
        Assert.True(TransactionMatcher.CandidateScore(Kind.Bill, 185000, due, Tx(-185000, "RENT", 20), 30, false)
            < TransactionMatcher.CandidateScore(Kind.Bill, 185000, due, Tx(-185000, "RENT", 12), 30, false));
        Assert.True(TransactionMatcher.CandidateScore(Kind.Bill, 10000, due, Tx(-12000, "X", 10), 30, false)
            < TransactionMatcher.CandidateScore(Kind.Bill, 10000, due, Tx(-10100, "X", 10), 30, false));
    }

    [Fact]
    public void Candidate_score_requires_sign_kind_window_and_a_plausible_amount()
    {
        var due = new DateOnly(2026, 10, 10);

        Assert.Null(TransactionMatcher.CandidateScore(Kind.Income, 185000, due, Tx(-185000, "RENT", 10), 30, false));
        Assert.Null(TransactionMatcher.CandidateScore(Kind.Bill, 185000, due, Tx(185000, "RENT", 10), 30, false));
        Assert.Null(TransactionMatcher.CandidateScore(Kind.Bill, 185000, due, Tx(-185000, "RENT", 25), 10, false));
        Assert.Null(TransactionMatcher.CandidateScore(Kind.Bill, 10000, due, Tx(-50000, "X", 10), 30, false));
        Assert.NotNull(TransactionMatcher.CandidateScore(Kind.Bill, 10000, due, Tx(-50000, "X", 10), 30, ruleHit: true));
        Assert.InRange(TransactionMatcher.CandidateScore(Kind.Income, 320000, due, Tx(320000, "PAYROLL", 1), 30, false)!.Value, 0, 100);
    }

    // ---------------------------------------------------------------------------------------------
    // Transfer analysis
    // ---------------------------------------------------------------------------------------------

    private static readonly DateOnly MonthStart = new(2026, 10, 1);
    private static readonly DateOnly MonthEnd = new(2026, 10, 31);

    [Fact]
    public void Pairs_flows_and_net_by_account()
    {
        Guid o1 = Guid.NewGuid(), i1 = Guid.NewGuid(), o2 = Guid.NewGuid(), i2 = Guid.NewGuid();
        TransferLeg[] legs =
        [
            new(o1, Checking, "Checking", new DateOnly(2026, 10, 3), -50000, i1, true),
            new(i1, Savings, "Savings", new DateOnly(2026, 10, 4), 50000, o1, true),
            new(o2, Checking, "Checking", new DateOnly(2026, 10, 17), -30000, i2, false),
            new(i2, Savings, "Savings", new DateOnly(2026, 10, 17), 30000, o2, false),
        ];

        var result = TransferAnalysis.Analyse(legs, MonthStart, MonthEnd);

        Assert.Equal(2, result.Pairs.Count);
        Assert.Equal(80000, result.TotalCents);
        var flow = Assert.Single(result.Flows);
        Assert.Equal(("Checking", "Savings", 80000L, 2), (flow.FromName, flow.ToName, flow.TotalCents, flow.Count));
        Assert.Equal(-80000, result.NetByAccount.Single(n => n.AccountId == Checking).NetCents);
        Assert.Equal(80000, result.NetByAccount.Single(n => n.AccountId == Savings).InCents);
        Assert.Equal(o1, result.Pairs.Single(p => p.Auto).From.Id);
        Assert.Empty(result.OneSided);
    }

    [Fact]
    public void One_sided_transfers_flow_to_or_from_other_account()
    {
        var leg = new TransferLeg(Guid.NewGuid(), Checking, "Checking", new DateOnly(2026, 10, 9), -100000, null, false);
        var incoming = new TransferLeg(Guid.NewGuid(), Checking, "Checking", new DateOnly(2026, 10, 12), 25000, null, false);

        var result = TransferAnalysis.Analyse([leg, incoming], MonthStart, MonthEnd);

        Assert.Equal(2, result.OneSided.Count);
        Assert.Contains(result.Flows, f => f.FromName == "Checking" && f.ToName == TransferAnalysis.OtherAccount && f.TotalCents == 100000);
        Assert.Contains(result.Flows, f => f.FromName == TransferAnalysis.OtherAccount && f.ToAccountId == Checking && f.TotalCents == 25000);
        var net = Assert.Single(result.NetByAccount);
        Assert.Equal((25000L, 100000L, -75000L), (net.InCents, net.OutCents, net.NetCents));
    }

    [Fact]
    public void A_pair_belongs_to_the_month_of_its_outflow_and_a_broken_link_is_one_sided()
    {
        Guid o = Guid.NewGuid(), i = Guid.NewGuid(), stray = Guid.NewGuid();
        TransferLeg[] legs =
        [
            new(o, Checking, "Checking", new DateOnly(2026, 9, 30), -50000, i, false),
            new(i, Savings, "Savings", new DateOnly(2026, 10, 1), 50000, o, false),
            new(stray, Savings, "Savings", new DateOnly(2026, 10, 2), 1000, Guid.NewGuid(), false),
        ];

        var result = TransferAnalysis.Analyse(legs, MonthStart, MonthEnd);

        Assert.Empty(result.Pairs);
        Assert.Equal(stray, Assert.Single(result.OneSided).Id);
    }

    // ---------------------------------------------------------------------------------------------
    // Bill payments
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Payment_history_is_the_next_occurrence_then_the_last_n_newest_first()
    {
        var rule = new Recurrence(Frequency.Monthly, new DateOnly(2025, 1, 15), null);

        var dates = PaymentHistory.Dates(rule, new DateOnly(2026, 10, 20), 12);

        Assert.Equal(13, dates.Count);
        Assert.Equal(new DateOnly(2026, 11, 15), dates[0]);
        Assert.Equal(new DateOnly(2026, 10, 15), dates[1]);
        Assert.Equal(new DateOnly(2025, 11, 15), dates[^1]);
        Assert.True(dates.Skip(1).Zip(dates.Skip(2)).All(p => p.First > p.Second));
    }

    [Fact]
    public void Payment_history_of_a_young_item_or_a_one_off()
    {
        var young = PaymentHistory.Dates(new Recurrence(Frequency.Weekly, new DateOnly(2026, 10, 10), null), new DateOnly(2026, 10, 20), 12);
        Assert.Equal([new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 10)], young);

        var once = PaymentHistory.Dates(new Recurrence(Frequency.Once, new DateOnly(2026, 10, 25), null), new DateOnly(2026, 10, 20), 12);
        Assert.Equal([new DateOnly(2026, 10, 25)], once);

        var ended = PaymentHistory.Dates(new Recurrence(Frequency.Monthly, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1)), new DateOnly(2026, 10, 20), 2);
        Assert.Equal([new DateOnly(2026, 3, 1), new DateOnly(2026, 2, 1)], ended);
    }

    // ---------------------------------------------------------------------------------------------
    // Spendable
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Spendable_uses_available_balance_for_checking_and_cash_only()
    {
        SpendableAccount[] accounts =
        [
            new(AccountType.Checking, 245050, 240000),
            new(AccountType.Checking, 10000, null),
            new(AccountType.Cash, 3000, null),
            new(AccountType.Savings, 500000, 500000),
            new(AccountType.Credit, 54000, 146000),
        ];

        Assert.Equal(240000 + 10000 + 3000, WeekEngine.Spendable(accounts));
        Assert.Equal(0, WeekEngine.Spendable([]));
    }

    // ---------------------------------------------------------------------------------------------
    // Validators
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Match_request_needs_item_and_due_date()
    {
        Assert.Empty(Fields(new InboxMatchRequestValidator(), new InboxMatchRequest(Guid.NewGuid(), new DateOnly(2026, 10, 1))));
        Assert.Equal(["dueDate", "itemId"], Fields(new InboxMatchRequestValidator(), new InboxMatchRequest(null, null)));
        Assert.Equal(["itemId"], Fields(new InboxMatchRequestValidator(), new InboxMatchRequest(Guid.Empty, new DateOnly(2026, 10, 1))));
    }

    [Fact]
    public void Transfer_request_peer_is_optional_but_not_empty()
    {
        Assert.Empty(Fields(new InboxTransferRequestValidator(), new InboxTransferRequest(null)));
        Assert.Empty(Fields(new InboxTransferRequestValidator(), new InboxTransferRequest(Guid.NewGuid())));
        Assert.Equal(["peerId"], Fields(new InboxTransferRequestValidator(), new InboxTransferRequest(Guid.Empty)));
    }

    [Fact]
    public void From_transaction_request()
    {
        Assert.Empty(Fields(new FromTransactionRequestValidator(), new FromTransactionRequest(Guid.NewGuid(), null, null, null)));
        Assert.Empty(Fields(new FromTransactionRequestValidator(), new FromTransactionRequest(Guid.NewGuid(), "Gym", Kind.Subscription, Frequency.Monthly)));
        Assert.Equal(["transactionId"], Fields(new FromTransactionRequestValidator(), new FromTransactionRequest(null, null, null, null)));
        Assert.Equal(["name"], Fields(new FromTransactionRequestValidator(), new FromTransactionRequest(Guid.NewGuid(), new string('x', 81), null, null)));
        Assert.Equal(["kind"], Fields(new FromTransactionRequestValidator(), new FromTransactionRequest(Guid.NewGuid(), null, (Kind)42, null)));
    }

    [Fact]
    public void Link_request_needs_a_transaction()
    {
        Assert.Empty(Fields(new LinkTransactionRequestValidator(), new LinkTransactionRequest(Guid.NewGuid())));
        Assert.Equal(["transactionId"], Fields(new LinkTransactionRequestValidator(), new LinkTransactionRequest(null)));
    }

    [Fact]
    public void Auto_ignore_transfers_setting_accepts_booleans_only()
    {
        static HouseholdSettingsRequest Settings(Optional<bool?> value) => new(default, default, default, default, default, default, default, default, value, default, default);

        Assert.Empty(Fields(new HouseholdSettingsRequestValidator(), Settings(Optional<bool?>.Of(false))));
        Assert.Empty(Fields(new HouseholdSettingsRequestValidator(), Settings(Optional<bool?>.Missing)));
        Assert.Equal(["autoIgnoreTransfers"], Fields(new HouseholdSettingsRequestValidator(), Settings(Optional<bool?>.Of(null))));
    }

    [Fact]
    public void Activity_summaries_for_v5_kinds()
    {
        Assert.Equal("THE DINER posted · $40.00 → $48.00", ActivitySummaries.TransactionPosted("THE DINER", -4000, -4800, "USD"));
        Assert.Equal("SHELL OIL · -$1.00 authorization dropped by the bank", ActivitySummaries.TransactionDropped("SHELL OIL", -100, "USD"));
        Assert.Equal("ignored 2 transfers (auto)", ActivitySummaries.TransferAuto(2));
        Assert.Equal("marked a transfer Checking → Other account · $500.00", ActivitySummaries.TransferPaired("Checking", null, 50000, "USD"));
        Assert.Equal("unmatched THE DINER · -$48.00", ActivitySummaries.TransactionUnmatched("THE DINER", -4800, "USD"));
    }

    [Fact]
    public void Like_patterns_escape_wildcards()
    {
        Assert.Equal(@"50\%", Data.ImportRepository.EscapeLike("50%"));
        Assert.Equal(@"a\_b\\c", Data.ImportRepository.EscapeLike(@"a_b\c"));
    }
}
