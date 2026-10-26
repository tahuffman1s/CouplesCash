using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Matching;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using FluentValidation;

namespace CouplesCash.Api.Tests;

/// <summary>v6: bank names + learning, validation and legacy payeeHint, rematch debounce/rate limit, found bills, pending grouping, built-in detail.</summary>
public class V6Tests
{
    private static string[] Fields<T>(AbstractValidator<T> validator, T value) =>
        ValidationFilter<object>.ToErrors(validator.Validate(value)).Keys.Order().ToArray();

    private static RecurringRequest Recurring(string[]? bankNames = null, string? payeeHint = null, bool? amountVaries = null) =>
        new("Rent", Kind.Bill, 185000, null, null, Frequency.Monthly, new DateOnly(2026, 11, 1), null, null, null, null, payeeHint, bankNames, amountVaries);

    // ---------------------------------------------------------------------------------------------
    // Bank names: validation, legacy payeeHint
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Bank_names_are_trimmed_and_case_insensitive_duplicates_dropped()
    {
        Assert.Equal(["Oakwood", "OAKWOOD PROPERTY"], BankNames.FromRequest(["  Oakwood ", "oakwood", "OAKWOOD PROPERTY", "OAKWOOD property"], payeeHint: "ignored"));
        Assert.Null(BankNames.Validate(["Oakwood", "oakwood", "OAKWOOD"])); // 3 spellings of one name are not "too many"
    }

    [Theory]
    [InlineData(11, 5, "At most 10 bank names.")]
    [InlineData(1, 1, "Each bank name must be 2–80 characters.")]
    [InlineData(1, 81, "Each bank name must be 2–80 characters.")]
    [InlineData(10, 80, null)]
    [InlineData(1, 2, null)]
    public void Bank_name_limits(int count, int length, string? expected)
    {
        var names = Enumerable.Range(0, count).Select(i => (i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 80))[..length]).ToArray();
        Assert.Equal(expected, BankNames.Validate(names));
    }

    [Fact]
    public void A_blank_bank_name_is_rejected_but_not_sending_the_list_is_fine()
    {
        Assert.NotNull(BankNames.Validate(["Oakwood", "   "]));
        Assert.Null(BankNames.Validate(null));
    }

    [Fact]
    public void Legacy_payee_hint_maps_into_bank_names_only_when_bank_names_are_not_sent()
    {
        Assert.Equal(["Jiffy Lube"], BankNames.FromRequest(null, "  Jiffy Lube "));
        Assert.Empty(BankNames.FromRequest(null, null));   // v4 PUT without a hint clears it
        Assert.Empty(BankNames.FromRequest(null, "   "));
        Assert.Equal(["Oakwood"], BankNames.FromRequest(["Oakwood"], "Jiffy Lube"));
        Assert.Empty(BankNames.FromRequest([], "Jiffy Lube")); // an explicit empty list wins
    }

    [Fact]
    public void Recurring_validator_names_bankNames_and_keeps_the_payeeHint_limit()
    {
        var validator = new RecurringRequestValidator();
        Assert.Empty(Fields(validator, Recurring(["Oakwood", "OAKWOOD PROPERTY"], amountVaries: true)));
        Assert.Equal(["bankNames"], Fields(validator, Recurring([.. Enumerable.Range(0, 11).Select(i => $"Name {i}")])));
        Assert.Equal(["bankNames"], Fields(validator, Recurring(["x"])));
        Assert.Equal(["payeeHint"], Fields(validator, Recurring(payeeHint: new string('x', 81))));
    }

    [Fact]
    public void Household_settings_auto_match_level_must_be_a_known_value()
    {
        var validator = new HouseholdSettingsRequestValidator();
        HouseholdSettingsRequest With(Optional<AutoMatchLevel?> level) => new(default, default, default, default, default, default, default, default, default, level);

        Assert.Empty(Fields(validator, With(Optional<AutoMatchLevel?>.Of(AutoMatchLevel.Off))));
        Assert.Empty(Fields(validator, With(default)));
        Assert.Equal(["autoMatchLevel"], Fields(validator, With(Optional<AutoMatchLevel?>.Of(null))));
        Assert.Equal(["autoMatchLevel"], Fields(validator, With(Optional<AutoMatchLevel?>.Of((AutoMatchLevel)9))));
    }

    [Fact]
    public void Bill_suggestion_accept_overrides_are_validated()
    {
        var validator = new BillSuggestionAcceptRequestValidator();
        Assert.Empty(Fields(validator, new BillSuggestionAcceptRequest(null, null, null, null, null)));
        Assert.Empty(Fields(validator, new BillSuggestionAcceptRequest("Netflix", Kind.Subscription, 1549, Frequency.Monthly, new DateOnly(2026, 11, 7))));
        Assert.Equal(["amountCents", "name"], Fields(validator, new BillSuggestionAcceptRequest("", null, 0, null, null)));
    }

    // ---------------------------------------------------------------------------------------------
    // Learning
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Learned_form_is_the_upper_cased_payee_normaliser()
    {
        Assert.Equal("OAKWOOD PROPERTY MGMT RENT", BankNames.LearnedForm(null, "ACH DEBIT OAKWOOD PROPERTY MGMT RENT"));
        Assert.Equal("JIFFY LUBE", BankNames.LearnedForm("Jiffy Lube", "JIFFY LUBE #42"));
        Assert.Equal("KROGER", BankNames.LearnedForm(null, "POS DEBIT 1234 KROGER #123 10/02"));
        Assert.Null(BankNames.LearnedForm(null, "X"));
    }

    [Fact]
    public void Learning_appends_a_new_name()
    {
        var learned = BankNames.Learn(["Oakwood"], [], "OAKWOOD PROPERTY MGMT RENT");
        Assert.Equal(["OAKWOOD PROPERTY MGMT RENT"], learned);
    }

    [Theory]
    [InlineData("JIFFY LUBE")]   // same as the user name once normalised
    [InlineData("jiffy  lube")]
    [InlineData("Jiffy Lube #42")]
    [InlineData("SHELL OIL")]    // already learned
    [InlineData("")]
    public void Learning_dedupes_against_both_lists(string candidate) =>
        Assert.Null(BankNames.Learn(["Jiffy Lube"], ["SHELL OIL"], candidate));

    [Fact]
    public void Learning_keeps_ten_learned_names_evicting_the_oldest_and_never_user_names()
    {
        string[] user = ["Rent", "Oakwood"];
        var learned = Enumerable.Range(1, 10).Select(i => $"PAYEE {(char)('A' + i)}").ToList();

        var next = BankNames.Learn(user, learned, "PAYEE NEW");

        Assert.NotNull(next);
        Assert.Equal(BankNames.MaxLearned, next.Count);
        Assert.Equal("PAYEE C", next[0]);          // "PAYEE B" (the oldest) was evicted
        Assert.Equal("PAYEE NEW", next[^1]);
        Assert.Equal(["Rent", "Oakwood"], user); // the user list is never touched
    }

    [Fact]
    public void Matcher_names_are_item_name_then_user_then_learned_without_duplicates() =>
        Assert.Equal(["Rent", "Oakwood", "OAKWOOD PROPERTY MGMT RENT"], BankNames.AllNames("Rent", ["Oakwood", "RENT"], ["OAKWOOD PROPERTY MGMT RENT", "oakwood"]));

    // ---------------------------------------------------------------------------------------------
    // Rematch debounce and rate limit
    // ---------------------------------------------------------------------------------------------

    private static readonly DateTime T0 = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Debounce_runs_ten_seconds_after_the_last_request()
    {
        var debouncer = new RematchDebouncer(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        var household = Guid.NewGuid();

        debouncer.Request(household, MatchingJobKind.Rematch, T0);
        debouncer.Request(household, MatchingJobKind.Rematch, T0.AddSeconds(4));

        Assert.Empty(debouncer.TakeDue(T0.AddSeconds(10)));
        Assert.Equal(T0.AddSeconds(14), debouncer.NextDue);
        Assert.Equal([(household, MatchingJobKind.Rematch)], debouncer.TakeDue(T0.AddSeconds(14)));
        Assert.Null(debouncer.NextDue);
    }

    [Fact]
    public void A_steady_stream_of_requests_still_runs_within_the_max_wait()
    {
        var debouncer = new RematchDebouncer(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        var household = Guid.NewGuid();
        for (var s = 0; s <= 90; s += 5)
        {
            debouncer.Request(household, MatchingJobKind.Rematch, T0.AddSeconds(s));
            if (debouncer.TakeDue(T0.AddSeconds(s)).Count > 0)
            {
                Assert.Equal(60, s);
                return;
            }
        }

        Assert.Fail("never ran");
    }

    [Fact]
    public void Households_and_job_kinds_are_debounced_independently()
    {
        var debouncer = new RematchDebouncer();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        debouncer.Request(a, MatchingJobKind.Rematch, T0);
        debouncer.Request(a, MatchingJobKind.FoundBills, T0.AddSeconds(5));
        debouncer.Request(b, MatchingJobKind.Rematch, T0.AddSeconds(8));

        Assert.Equal([(a, MatchingJobKind.Rematch)], debouncer.TakeDue(T0.AddSeconds(10)));
        Assert.Equal([(a, MatchingJobKind.FoundBills), (b, MatchingJobKind.Rematch)], debouncer.TakeDue(T0.AddSeconds(20)));
        Assert.Equal(0, debouncer.Count);
    }

    [Fact]
    public void Manual_rematch_is_limited_to_once_a_minute()
    {
        Assert.Null(RematchRules.RetryAfter(null, T0));
        Assert.Equal(TimeSpan.FromSeconds(45), RematchRules.RetryAfter(T0, T0.AddSeconds(15)));
        Assert.Null(RematchRules.RetryAfter(T0, T0.AddMinutes(1)));
    }

    [Fact]
    public void Manual_rematch_queue_answers_retry_after_for_the_second_request_of_the_same_household()
    {
        var queue = new MatchingQueue(TimeProvider.System);
        var household = Guid.NewGuid();

        Assert.Null(queue.TryRequestManual(household));
        Assert.NotNull(queue.TryRequestManual(household));
        Assert.Null(queue.TryRequestManual(Guid.NewGuid()));
        Assert.True(queue.Reader.TryRead(out var job));
        Assert.Equal((household, MatchingJobKind.Rematch), job);
    }

    [Fact]
    public void Sweeps_are_due_after_the_interval()
    {
        var interval = TimeSpan.FromMinutes(30);
        Assert.True(RematchRules.IsDue(null, T0, interval));
        Assert.False(RematchRules.IsDue(T0, T0.AddMinutes(29), interval));
        Assert.True(RematchRules.IsDue(T0, T0.AddMinutes(30), interval));
        Assert.True(RematchRules.IsDue(T0, T0, TimeSpan.Zero));
    }

    // ---------------------------------------------------------------------------------------------
    // Found bills
    // ---------------------------------------------------------------------------------------------

    private static DetectedBill Bill(string bankName, Frequency frequency = Frequency.Monthly, int score = 80, string? name = null) =>
        new(name ?? bankName, bankName, Kind.Subscription, frequency, 1549, false, new DateOnly(2026, 11, 7), score, [Guid.NewGuid()]);

    [Fact]
    public void Fingerprint_is_the_normalised_bank_name_plus_frequency()
    {
        Assert.Equal(BillSuggestionRules.Fingerprint("NETFLIX", Frequency.Monthly), BillSuggestionRules.Fingerprint("  netflix ", Frequency.Monthly));
        Assert.Equal(BillSuggestionRules.Fingerprint("SQ *BLUE BOTTLE #42", Frequency.Weekly), BillSuggestionRules.Fingerprint("blue bottle", Frequency.Weekly));
        Assert.NotEqual(BillSuggestionRules.Fingerprint("NETFLIX", Frequency.Monthly), BillSuggestionRules.Fingerprint("NETFLIX", Frequency.Quarterly));
        Assert.NotEqual(BillSuggestionRules.Fingerprint("NETFLIX", Frequency.Monthly), BillSuggestionRules.Fingerprint("SPOTIFY", Frequency.Monthly));
        Assert.Equal(32, BillSuggestionRules.Fingerprint("NETFLIX", Frequency.Monthly).Length);
    }

    [Fact]
    public void Dismissed_and_accepted_fingerprints_never_come_back_open_ones_refresh_and_stale_ones_go()
    {
        var netflix = Bill("NETFLIX");
        var spotify = Bill("SPOTIFY");
        var gym = Bill("PLANET FITNESS");
        var water = Bill("CITY WATER");
        var staleId = Guid.NewGuid();
        var openId = Guid.NewGuid();
        StoredBillSuggestion[] stored =
        [
            new(Guid.NewGuid(), spotify.Fingerprint, BillSuggestionRules.Dismissed),
            new(Guid.NewGuid(), gym.Fingerprint, BillSuggestionRules.Accepted),
            new(openId, water.Fingerprint, BillSuggestionRules.Open),
            new(staleId, Bill("OLD GYM").Fingerprint, BillSuggestionRules.Open),
        ];

        var plan = BillSuggestionRules.Plan(stored, [netflix, spotify, gym, water]);

        Assert.Equal(["CITY WATER", "NETFLIX"], plan.Upserts.Select(b => b.BankName).Order().ToArray());
        Assert.Equal([staleId], plan.RemoveOpen);
    }

    [Fact]
    public void A_duplicate_fingerprint_in_one_run_keeps_the_best_score()
    {
        var plan = BillSuggestionRules.Plan([], [Bill("NETFLIX", score: 70, name: "a"), Bill("netflix", score: 92, name: "b")]);
        Assert.Equal("b", Assert.Single(plan.Upserts).Name);
    }

    [Theory]
    [InlineData(Frequency.Monthly, "2026-11-07", "2026-07-30", "2026-07-07")]
    [InlineData(Frequency.Monthly, "2026-11-07", "2026-08-07", "2026-08-07")]
    [InlineData(Frequency.Monthly, "2026-12-31", "2026-08-31", "2026-08-31")] // keeps the 31st (skips 30-day months)
    [InlineData(Frequency.Monthly, "2026-11-30", "2026-03-05", "2026-01-30")] // Feb would clamp to the 28th: go back to January
    [InlineData(Frequency.Weekly, "2026-11-02", "2026-10-10", "2026-10-05")]
    [InlineData(Frequency.Biweekly, "2026-11-06", "2026-09-01", "2026-08-28")]
    [InlineData(Frequency.Quarterly, "2026-12-15", "2026-03-20", "2026-03-15")]
    [InlineData(Frequency.Monthly, "2026-11-07", null, "2026-11-07")]
    public void Accepted_items_are_anchored_on_or_before_the_first_sample(Frequency frequency, string nextDue, string? earliest, string expected) =>
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            BillSuggestionRules.AnchorFor(frequency, DateOnly.Parse(nextDue, System.Globalization.CultureInfo.InvariantCulture),
                earliest is null ? null : DateOnly.Parse(earliest, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Samples_match_their_nearest_occurrence_within_the_window()
    {
        var rule = new Recurrence(Frequency.Monthly, new DateOnly(2026, 7, 26), null);
        var window = BillSuggestionRules.MatchWindowDays(Frequency.Monthly);

        Assert.Equal(new DateOnly(2026, 7, 26), BillSuggestionRules.NearestOccurrence(rule, new DateOnly(2026, 7, 31), window));
        Assert.Equal(new DateOnly(2026, 8, 26), BillSuggestionRules.NearestOccurrence(rule, new DateOnly(2026, 8, 28), window));
        Assert.Null(BillSuggestionRules.NearestOccurrence(rule, new DateOnly(2026, 8, 11), window));
    }

    [Fact]
    public void Matcher_suggestions_are_parsed_and_unknown_transactions_or_values_dropped()
    {
        var known = Guid.NewGuid();
        var rows = new Dictionary<Guid, DetectionRow> { [known] = new() { Id = known, PostedOn = new DateOnly(2026, 10, 7), AmountCents = -1549 } };

        var ok = BillSuggestionService.ToDetected(new MatcherRecurringSuggestion("Netflix", "NETFLIX", "subscription", "monthly", 1549, false,
            new DateOnly(2026, 11, 7), 92, [known.ToString(), "not-a-guid", Guid.NewGuid().ToString()]), rows);

        Assert.NotNull(ok);
        Assert.Equal((Kind.Subscription, Frequency.Monthly, 1549L, 92), (ok.Kind, ok.Frequency, ok.AmountCents, ok.Score));
        Assert.Equal([known], ok.TransactionIds);
        Assert.Null(BillSuggestionService.ToDetected(new MatcherRecurringSuggestion("X", "X", "subscription", "fortnightly", 1, false, default, 1, [known.ToString()]), rows));
        Assert.Null(BillSuggestionService.ToDetected(new MatcherRecurringSuggestion("X", "X", "bill", "monthly", 100, false, default, 1, [Guid.NewGuid().ToString()]), rows));
    }

    // ---------------------------------------------------------------------------------------------
    // Pending view grouping
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Pending_groups_by_mapped_account_with_pending_accounts_first_by_outflow()
    {
        var checking = new PendingAccount(Guid.NewGuid(), null, "Checking", "Bank", 245050, 240000, Linked: true);
        var visa = new PendingAccount(Guid.NewGuid(), null, "Visa", "Bank", 54000, null, Linked: true);
        var savings = new PendingAccount(Guid.NewGuid(), null, "Savings", "Bank", 1000000, null, Linked: true);
        var cash = new PendingAccount(Guid.NewGuid(), null, "Cash", "", 5000, null, Linked: false);
        var unmappedExternal = Guid.NewGuid();
        var unmapped = new PendingAccount(null, unmappedExternal, "Brokerage", "Broker", 0, null, Linked: false);
        PendingEntry<string>[] entries =
        [
            new("coffee", checking.AccountId, null, -450),
            new("refund", checking.AccountId, null, 2000),
            new("hotel", visa.AccountId, null, -30000),
            new("fee", null, unmappedExternal, -100),
            new("orphan", Guid.NewGuid(), null, -1),
        ];

        var groups = PendingGrouping.Group([checking, visa, savings, cash, unmapped], entries);

        Assert.Equal(["Visa", "Checking", "Brokerage", "Other", "Savings"], groups.Select(g => g.Account.Name).ToArray());
        Assert.Equal((2, 450L, 2000L), (groups[1].Count, groups[1].OutCents, groups[1].InCents));
        Assert.Equal(["coffee", "refund"], groups[1].Items);
        Assert.Null(groups[2].Account.AccountId);
        Assert.Equal(0, groups[^1].Count); // linked accounts are listed without pending rows; the unlinked cash account is not
    }

    // ---------------------------------------------------------------------------------------------
    // Built-in fallback: amountVaries, detail, levels
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Amount_varies_widens_the_builtin_window_and_tolerance()
    {
        var item = Guid.NewGuid();
        var tx = new MatchTransaction(Guid.NewGuid(), null, new DateOnly(2026, 10, 29), -6000, "CITY WATER UTIL", null);
        var due = new DateOnly(2026, 10, 20);

        Assert.Null(TransactionMatcher.FindOccurrence(tx, [new MatchableOccurrence(item, "Water", Kind.Bill, 4500, due)]));
        Assert.NotNull(TransactionMatcher.FindOccurrence(tx, [new MatchableOccurrence(item, "Water", Kind.Bill, 4500, due, AmountVaries: true)]));
        Assert.Equal(1575, TransactionMatcher.Tolerance(4500, varies: true));      // max(35 %, $15)
        Assert.Equal(1500, TransactionMatcher.Tolerance(1000, varies: true));
    }

    [Fact]
    public void Builtin_detail_uses_the_contract_formulas()
    {
        // README sample: exact amount, 2 days early on a monthly item → date 84.
        var tx = new MatchTransaction(Guid.NewGuid(), null, new DateOnly(2026, 10, 30), -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT RENT", null);
        var occurrence = new MatchableOccurrence(Guid.NewGuid(), "Rent", Kind.Bill, 185000, new DateOnly(2026, 11, 1));

        var detail = MatchingRules.BuiltinOccurrenceDetail(tx, occurrence, ["Rent", "Oakwood"], ruleNamesItem: false);

        Assert.Equal((100, 100, 84, MatchDetail.Builtin), (detail.Name, detail.Amount, detail.Date, detail.Engine));
        Assert.Equal("name 100% · amount exact · 2 days early", detail.Reason);
        Assert.Equal(" · learned payee", MatchingRules.BuiltinOccurrenceDetail(tx, occurrence, [], ruleNamesItem: true).Reason[^16..]);
    }

    [Theory]
    [InlineData(0, 100, 6000, 100)]
    [InlineData(580, 1000, 6000, 83)]    // one-off Oil change 60.00 paid 54.20
    [InlineData(1000, 1000, 6000, 70)]   // at the edge
    [InlineData(1300, 1000, 6000, 50)]   // 5 % outside → 70 − 4·5
    [InlineData(2200, 1000, 6000, 0)]    // 20 % outside → max(0, 70 − 80)
    [InlineData(9000, 1000, 6000, 0)]
    public void Amount_score(long diff, long allowed, long expected, int score) => Assert.Equal(score, MatchingRules.AmountScore(diff, allowed, expected));

    [Theory]
    [InlineData(0, 5, 100)]
    [InlineData(-2, 5, 84)]
    [InlineData(5, 5, 60)]
    [InlineData(6, 5, 0)]
    [InlineData(8, 10, 68)]
    public void Date_score(int days, int window, int score) => Assert.Equal(score, MatchingRules.DateScore(days, window));

    [Fact]
    public void Levels_gate_the_automatic_actions()
    {
        var strong = Suggestion.Occurrence(Guid.NewGuid(), new DateOnly(2026, 11, 1), 95);
        Assert.True(MatchingRules.BuiltinAuto(AutoMatchLevel.Bills, strong, ruleNamesItem: true));
        Assert.False(MatchingRules.BuiltinAuto(AutoMatchLevel.Off, strong, ruleNamesItem: true));
        Assert.False(MatchingRules.BuiltinAuto(AutoMatchLevel.All, strong, ruleNamesItem: false));
        Assert.False(MatchingRules.BuiltinAuto(AutoMatchLevel.All, Suggestion.Occurrence(Guid.NewGuid(), new DateOnly(2026, 11, 1), 85), ruleNamesItem: true));

        Assert.True(MatchingRules.AutoAppliesCategories(AutoMatchLevel.All));
        Assert.False(MatchingRules.AutoAppliesCategories(AutoMatchLevel.Bills));
        Assert.True(MatchingRules.AutoIgnoresTransfers(AutoMatchLevel.Bills, autoIgnoreTransfers: true));
        Assert.False(MatchingRules.AutoIgnoresTransfers(AutoMatchLevel.Off, autoIgnoreTransfers: true));
        Assert.False(MatchingRules.AutoIgnoresTransfers(AutoMatchLevel.All, autoIgnoreTransfers: false));
    }

    [Fact]
    public void Candidate_range_covers_ten_days_around_the_batch() =>
        Assert.Equal((new DateOnly(2026, 9, 21), new DateOnly(2026, 11, 10)),
            MatchingRules.CandidateRange([new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 1)]));

    [Fact]
    public void Match_detail_round_trips_through_the_jsonb_column_and_bad_json_reads_as_null()
    {
        var json = MappingV3.SerializeDetail(new MatchDetail(91, 100, 82, "name 91% · amount exact · 8 days early", MatchDetail.Fuzzy));

        Assert.Equal("""{"name":91,"amount":100,"date":82,"reason":"name 91% · amount exact · 8 days early","engine":"fuzzy"}""", System.Text.RegularExpressions.Regex.Unescape(json));
        Assert.Equal(new MatchDetailDto(91, 100, 82, "name 91% · amount exact · 8 days early", "fuzzy"), MappingV3.ParseDetail(json));
        Assert.Null(MappingV3.ParseDetail("{not json"));
        Assert.Null(MappingV3.ParseDetail(null));
    }

    [Fact]
    public void Week_pending_out_counts_spendable_accounts_only()
    {
        AccountRow[] accounts =
        [
            new() { Type = AccountType.Checking, PendingOutCents = 525 },
            new() { Type = AccountType.Cash, PendingOutCents = 100 },
            new() { Type = AccountType.Credit, PendingOutCents = 30000 },
        ];
        Assert.Equal(625, WeekService.PendingOut(accounts));
    }

    [Fact]
    public void Request_records_have_no_default_parameter_values()
    {
        // ASP.NET OpenAPI serialises parameter defaults into the schema; an Optional<T> default makes
        // GET /openapi/v1.json throw (happened in v5 and v6). Keep request constructors default-free.
        var offenders = typeof(HouseholdSettingsRequest).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(HouseholdSettingsRequest).Namespace && t.Name.EndsWith("Request", StringComparison.Ordinal))
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters())
                .Where(p => p.HasDefaultValue && p.ParameterType.IsGenericType
                            && p.ParameterType.GetGenericTypeDefinition() == typeof(Optional<>))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();
        Assert.Empty(offenders);
    }
}
