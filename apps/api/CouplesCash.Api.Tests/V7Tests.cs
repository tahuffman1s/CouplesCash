using CouplesCash.Api.Contracts;
using CouplesCash.Api.Domain;
using CouplesCash.Api.SimpleFin;
using CouplesCash.Api.Validation;
using FluentValidation;

namespace CouplesCash.Api.Tests;

/// <summary>
/// v7 "Our Friday, automated": the payday-anchored pay week, the bills-account spendable fix, the Friday plan with this household's exact
/// numbers, the payday checklist, the savings sweep, planned transfers, the payday push and the v7 validators.
/// </summary>
public class V7Tests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static string[] Fields<T>(AbstractValidator<T> validator, T value) =>
        ValidationFilter<object>.ToErrors(validator.Validate(value)).Keys.Order().ToArray();

    // Accounts of the household (PLAN-V7 "Accounts have roles").
    private static readonly Guid UsBank = Guid.Parse("00000000-0000-7000-8000-0000000000b1");
    private static readonly Guid AllySavings = Guid.Parse("00000000-0000-7000-8000-0000000000b2");
    private static readonly Guid FunTravis = Guid.Parse("00000000-0000-7000-8000-0000000000b3");
    private static readonly Guid FunWife = Guid.Parse("00000000-0000-7000-8000-0000000000b4");
    private static readonly Guid Card = Guid.Parse("00000000-0000-7000-8000-0000000000b5");

    private static readonly IReadOnlyDictionary<Guid, AccountRole> Roles = new Dictionary<Guid, AccountRole>
    {
        [UsBank] = AccountRole.Bills,
        [AllySavings] = AccountRole.Savings,
        [FunTravis] = AccountRole.Fun,
        [FunWife] = AccountRole.Fun,
        [Card] = AccountRole.Other,
    };

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:000000000000}");

    // The pay week Friday 2026-10-30 … Thursday 2026-11-05 (US DST ends inside it, on Sunday 2026-11-01).
    private static readonly WeekWindow PayWeek = new(D(2026, 10, 30), D(2026, 11, 5));

    private static PlanOccurrence Bill(int id, long cents, DateOnly due, Guid? account, long? paid = null, Kind kind = Kind.Bill, Frequency frequency = Frequency.Monthly, bool fromBank = false) =>
        new(Id(id), kind, frequency, cents, due, paid, fromBank, account, null, null);

    private static PlanOccurrence Transfer(int id, long cents, DateOnly due, Guid from, Guid to, long? paid = null, Frequency frequency = Frequency.Weekly, bool fromBank = false) =>
        new(Id(id), Kind.Transfer, frequency, cents, due, paid, fromBank, from, from, to);

    private static PlanOccurrence Income(int id, long cents, DateOnly due, Guid? account, long? paid = null, bool fromBank = false) =>
        new(Id(id), Kind.Income, Frequency.Weekly, cents, due, paid, fromBank, account, null, null);

    /// <summary>Her note for the week of Friday 2026-10-30.</summary>
    private static readonly PlanOccurrence[] Week =
    [
        Bill(1, 185_000, D(2026, 11, 1), UsBank),                                        // Rent
        Bill(2, 8_500, D(2026, 11, 3), null),                                            // Phone (unassigned counts)
        Bill(3, 5_750, D(2026, 11, 4), UsBank, kind: Kind.Subscription),                 // Internet
        Bill(4, 1_549, D(2026, 10, 30), UsBank, paid: 1_549, kind: Kind.Subscription, fromBank: true), // Netflix, already paid → ticked
        Bill(5, 25_000, D(2026, 11, 2), AllySavings),                                    // Student loan from savings → not in the math
        Bill(6, 9_900, D(2026, 11, 2), Card),                                            // A card's fee → not on the bills account
        Transfer(7, 2_000, D(2026, 10, 30), UsBank, FunTravis),                          // $20 Travis
        Transfer(8, 2_000, D(2026, 10, 30), UsBank, FunWife),                            // $20 wife
        Bill(9, 6_000, D(2026, 10, 31), UsBank, frequency: Frequency.Once),             // oil change 60 saturday
        Income(10, 150_000, D(2026, 10, 30), UsBank, paid: 150_000, fromBank: true),    // this payday's paycheck, posted
        Income(10, 150_000, D(2026, 11, 6), UsBank),                                     // next payday's paycheck → the NEW week
        Bill(1, 185_000, D(2026, 12, 1), UsBank),                                        // next month's rent → outside
    ];

    // ---------------------------------------------------------------------------------------------
    // Pay week: payday-anchored, any weekday, across month / year boundaries and DST
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 10, 30, "2026-10-30", "2026-11-05")] // payday itself
    [InlineData(2026, 11, 5, "2026-10-30", "2026-11-05")]  // Thursday = the last day of the pay week (crosses the month)
    [InlineData(2026, 11, 6, "2026-11-06", "2026-11-12")]  // next payday starts the next week
    [InlineData(2026, 10, 26, "2026-10-23", "2026-10-29")] // a Monday
    [InlineData(2026, 12, 31, "2026-12-25", "2026-12-31")] // New Year's Eve (Thursday) closes the week of Christmas
    [InlineData(2027, 1, 1, "2027-01-01", "2027-01-07")]   // New Year's Day is a Friday payday
    [InlineData(2026, 1, 1, "2025-12-26", "2026-01-01")]   // a Thursday 1 January belongs to the week that started in December
    [InlineData(2024, 2, 29, "2024-02-23", "2024-02-29")]  // leap day
    public void Friday_anchored_pay_weeks(int y, int m, int d, string start, string end)
    {
        var week = WeekEngine.WeekBounds(D(y, m, d), 5);
        Assert.Equal(DateOnly.Parse(start), week.Start);
        Assert.Equal(DateOnly.Parse(end), week.End);
        Assert.Equal(DayOfWeek.Friday, week.Start.DayOfWeek);
        Assert.Equal(DayOfWeek.Thursday, week.End.DayOfWeek);
    }

    [Fact]
    public void Any_weekday_can_be_payday()
    {
        for (var payday = 0; payday <= 6; payday++)
        {
            for (var offset = 0; offset < 14; offset++)
            {
                var date = D(2026, 10, 25).AddDays(offset);
                var week = WeekEngine.WeekBounds(date, payday);
                Assert.Equal(payday, (int)week.Start.DayOfWeek);
                Assert.True(week.Contains(date));
                Assert.Equal(6, week.End.DayNumber - week.Start.DayNumber);
            }
        }
    }

    [Fact]
    public void The_week_spanning_the_DST_change_is_still_seven_days_and_payday_midnight_is_local()
    {
        // US DST ends Sunday 2026-11-01: payday 2026-10-30 starts at 05:00Z (CDT), the next one, 2026-11-06, at 06:00Z (CST).
        Assert.Equal(new DateTime(2026, 10, 30, 5, 0, 0, DateTimeKind.Utc), SchedulerRules.LocalMidnightUtc(D(2026, 10, 30), "America/Chicago"));
        Assert.Equal(new DateTime(2026, 11, 6, 6, 0, 0, DateTimeKind.Utc), SchedulerRules.LocalMidnightUtc(D(2026, 11, 6), "America/Chicago"));

        // 05:30Z on 2026-11-06 is still Thursday 23:30 in Chicago: not payday yet, still the old pay week.
        var thursdayNight = DateOnly.FromDateTime(SchedulerRules.ToLocal(new DateTime(2026, 11, 6, 5, 30, 0, DateTimeKind.Utc), "America/Chicago"));
        Assert.Equal(D(2026, 11, 5), thursdayNight);
        Assert.False(PlanEngine.IsPayday(thursdayNight, 5));
        Assert.Equal(D(2026, 10, 30), WeekEngine.WeekBounds(thursdayNight, 5).Start);

        // 06:00Z is payday 00:00 local: the new week (and the sweep freeze) begins.
        var payday = DateOnly.FromDateTime(SchedulerRules.ToLocal(new DateTime(2026, 11, 6, 6, 0, 0, DateTimeKind.Utc), "America/Chicago"));
        Assert.True(PlanEngine.IsPayday(payday, 5));
        Assert.Equal(D(2026, 11, 6), WeekEngine.WeekBounds(payday, 5).Start);

        // The spring-forward week (2027-03-14) is seven calendar days too.
        var spring = WeekEngine.WeekBounds(D(2027, 3, 14), 5);
        Assert.Equal((D(2027, 3, 12), D(2027, 3, 18)), (spring.Start, spring.End));
        Assert.Equal(new DateTime(2027, 3, 12, 6, 0, 0, DateTimeKind.Utc), SchedulerRules.LocalMidnightUtc(spring.Start, "America/Chicago"));
        Assert.Equal(new DateTime(2027, 3, 19, 5, 0, 0, DateTimeKind.Utc), SchedulerRules.LocalMidnightUtc(spring.End.AddDays(1), "America/Chicago"));
    }

    // ---------------------------------------------------------------------------------------------
    // Spendable: only the bills account
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Spendable_uses_only_the_bills_account_available_balance()
    {
        var accounts = new[]
        {
            new SpendableAccount(AccountType.Checking, 240_807, 234_567, AccountRole.Bills, 6_240),
            new SpendableAccount(AccountType.Checking, 11_000, 11_000, AccountRole.Fun),
            new SpendableAccount(AccountType.Checking, 9_500, null, AccountRole.Fun),
            new SpendableAccount(AccountType.Savings, 1_000_000, 1_000_000, AccountRole.Savings),
            new SpendableAccount(AccountType.Cash, 4_000, null, AccountRole.Other),
        };
        Assert.Equal(234_567, WeekEngine.Spendable(accounts));
    }

    [Fact]
    public void Without_a_bank_available_balance_the_bills_account_is_balance_minus_pending()
    {
        var accounts = new[]
        {
            new SpendableAccount(AccountType.Checking, 240_807, null, AccountRole.Bills, 6_240),
            new SpendableAccount(AccountType.Checking, 11_000, null, AccountRole.Fun),
        };
        Assert.Equal(234_567, WeekEngine.Spendable(accounts));
        Assert.Equal(234_567, WeekEngine.BillsAvailable(null, 240_807, 6_240));
        Assert.Equal(234_567, WeekEngine.BillsAvailable(234_567, 240_807, 6_240)); // the bank's available already nets pending: never twice
    }

    [Fact]
    public void Before_setup_the_v6_rule_still_applies()
    {
        var accounts = new[]
        {
            new SpendableAccount(AccountType.Checking, 240_807, 234_567, AccountRole.Other, 6_240),
            new SpendableAccount(AccountType.Checking, 11_000, null, AccountRole.Other),
            new SpendableAccount(AccountType.Savings, 1_000_000, null, AccountRole.Other),
        };
        Assert.Equal(234_567 + 11_000, WeekEngine.Spendable(accounts));
        Assert.Equal(WeekEngine.SpendableV6(accounts), WeekEngine.Spendable(accounts));
    }

    // ---------------------------------------------------------------------------------------------
    // The Friday plan: her numbers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Her_note_with_the_bank_available_balance()
    {
        // US Bank, available now $2,345.67 (bank-reported; the 3 pending −$62.40 are already in it).
        var available = WeekEngine.BillsAvailable(234_567, 240_807, 6_240);
        var totals = PlanEngine.Totals(PayWeek, available, Week, UsBank, Roles);

        Assert.Equal(199_250, totals.BillsOutCents);   // − Bills before next Friday $1,992.50
        Assert.Equal(4_000, totals.FunOutCents);       // − Fun money $40.00
        Assert.Equal(6_000, totals.OneOffOutCents);    // − One-offs $60.00
        Assert.Equal(0, totals.IncomeInCents);         // this payday's paycheck already posted; next payday's is the new week
        Assert.Equal(25_317, totals.LeftAtEndCents);   // = Left on Thursday $253.17
    }

    [Fact]
    public void Ticking_a_bill_by_hand_never_moves_left_on_thursday_only_the_bank_does()
    {
        // Rent ticked by hand (or by autopay) before the bank shows it: the $1,850 is still in available, so it still counts.
        var ticked = Week.Select(o => o.ItemId == Id(1) ? o with { PaidCents = 185_000 } : o).ToList();
        var before = PlanEngine.Totals(PayWeek, 234_567, Week, UsBank, Roles);
        var byHand = PlanEngine.Totals(PayWeek, 234_567, ticked, UsBank, Roles);
        Assert.Equal(before, byHand);

        // The bank row arrives at $1,862.40: available drops by it and the line is paid from the bank → left moves only by the difference.
        var fromBank = Week.Select(o => o.ItemId == Id(1) ? o with { PaidCents = 186_240, PaidFromBank = true } : o).ToList();
        var posted = PlanEngine.Totals(PayWeek, 234_567 - 186_240, fromBank, UsBank, Roles);
        Assert.Equal(199_250 - 185_000, posted.BillsOutCents);
        Assert.Equal(25_317 - 1_240, posted.LeftAtEndCents);
    }

    [Fact]
    public void Her_note_without_the_bank_available_balance()
    {
        // Balance $2,408.07 − pending $62.40 → the same $2,345.67 → the same $253.17.
        var available = WeekEngine.BillsAvailable(null, 240_807, 6_240);
        Assert.Equal(234_567, available);
        Assert.Equal(25_317, PlanEngine.Totals(PayWeek, available, Week, UsBank, Roles).LeftAtEndCents);
    }

    [Fact]
    public void Lines_are_bucketed_like_her_note()
    {
        PlanBucket Of(PlanOccurrence o) => PlanEngine.Classify(o, UsBank, Roles);
        Assert.Equal(PlanBucket.Bill, Of(Week[0]));
        Assert.Equal(PlanBucket.Bill, Of(Week[1]));        // unassigned
        Assert.Equal(PlanBucket.Bill, Of(Week[3]));        // paid: listed (ticked), not counted
        Assert.Equal(PlanBucket.SavingsBill, Of(Week[4])); // student loan
        Assert.Equal(PlanBucket.None, Of(Week[5]));        // another account's bill
        Assert.Equal(PlanBucket.FunMoney, Of(Week[6]));
        Assert.Equal(PlanBucket.OneOff, Of(Week[8]));
        Assert.Equal(PlanBucket.Income, Of(Week[9]));
        Assert.Equal("oneoff", PlanEngine.LineKind(PlanBucket.OneOff, Kind.Bill));
        Assert.Equal("transfer", PlanEngine.LineKind(PlanBucket.FunMoney, Kind.Transfer));
        Assert.Equal("subscription", PlanEngine.LineKind(PlanBucket.Bill, Kind.Subscription));
    }

    [Fact]
    public void Transfers_classify_by_their_accounts()
    {
        PlanBucket Of(PlanOccurrence o) => PlanEngine.Classify(o, UsBank, Roles);
        Assert.Equal(PlanBucket.FunMoney, Of(Transfer(1, 2_000, PayWeek.Start, UsBank, FunWife)));
        Assert.Equal(PlanBucket.Bill, Of(Transfer(2, 10_000, PayWeek.Start, UsBank, AllySavings)));   // out of the bills account, not fun
        Assert.Equal(PlanBucket.Income, Of(Transfer(3, 10_000, PayWeek.Start, AllySavings, UsBank))); // into the bills account
        Assert.Equal(PlanBucket.None, Of(Transfer(4, 10_000, PayWeek.Start, AllySavings, FunWife)));  // never touches the bills account
        Assert.Equal(PlanBucket.None, Of(Income(5, 1_000, PayWeek.Start, AllySavings)));              // interest on savings
    }

    [Fact]
    public void Unpaid_income_counts_only_inside_the_pay_week()
    {
        var occurrences = new[]
        {
            Income(1, 150_000, PayWeek.Start, UsBank),          // payday's paycheck has not posted yet → still to come this week
            Income(1, 150_000, PayWeek.Start.AddDays(7), UsBank), // next payday → the new week
            Bill(2, 10_000, PayWeek.End, UsBank),
        };
        var totals = PlanEngine.Totals(PayWeek, 50_000, occurrences, UsBank, Roles);
        Assert.Equal(150_000, totals.IncomeInCents);
        Assert.Equal(50_000 - 10_000 + 150_000, totals.LeftAtEndCents);
    }

    [Fact]
    public void Before_setup_every_non_savings_non_fun_account_is_the_bills_side()
    {
        var roles = new Dictionary<Guid, AccountRole> { [UsBank] = AccountRole.Other, [AllySavings] = AccountRole.Savings, [FunWife] = AccountRole.Fun };
        Assert.Equal(PlanBucket.Bill, PlanEngine.Classify(Bill(1, 100, PayWeek.Start, UsBank), null, roles));
        Assert.Equal(PlanBucket.SavingsBill, PlanEngine.Classify(Bill(2, 100, PayWeek.Start, AllySavings), null, roles));
        Assert.Equal(PlanBucket.FunMoney, PlanEngine.Classify(Transfer(3, 2_000, PayWeek.Start, UsBank, FunWife), null, roles));
        Assert.Equal(PlanBucket.None, PlanEngine.Classify(Bill(4, 100, PayWeek.Start, FunWife), null, roles));
    }

    [Fact]
    public void The_first_payday_of_the_month_adds_the_monthly_fun_money()
    {
        // $150 each anchored on the 1st (Sunday 2026-11-01, payday + 2) → fun money $340 that week, and on the checklist.
        var monthly = new[] { Transfer(20, 15_000, D(2026, 11, 1), UsBank, FunTravis, frequency: Frequency.Monthly), Transfer(21, 15_000, D(2026, 11, 1), UsBank, FunWife, frequency: Frequency.Monthly) };
        var all = Week.Concat(monthly).ToList();
        var totals = PlanEngine.Totals(PayWeek, 234_567, all, UsBank, Roles);
        Assert.Equal(34_000, totals.FunOutCents);
        Assert.Equal(25_317 - 30_000, totals.LeftAtEndCents);
        Assert.All(monthly, m => Assert.True(PlanEngine.IsChecklistFun(m, PlanBucket.FunMoney, PayWeek)));
        Assert.False(PlanEngine.IsChecklistFun(Transfer(22, 15_000, D(2026, 11, 4), UsBank, FunWife, frequency: Frequency.Monthly), PlanBucket.FunMoney, PayWeek)); // payday + 5
    }

    // ---------------------------------------------------------------------------------------------
    // Payday checklist
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, false, true)] // payday: always
    [InlineData(0, true, true)]
    [InlineData(2, false, true)] // payday + 2, something still open
    [InlineData(2, true, false)] // payday + 2, all done
    [InlineData(3, false, true)]
    [InlineData(4, false, false)] // payday + 4: gone
    public void Checklist_shows_on_payday_and_until_done(int daysAfterPayday, bool allDone, bool visible) =>
        Assert.Equal(visible, PlanEngine.ChecklistVisible(PayWeek, PayWeek.Start.AddDays(daysAfterPayday), allDone));

    [Fact]
    public void The_paycheck_item_is_payday_income()
    {
        Assert.True(PlanEngine.IsPaycheck(Week[9], PlanBucket.Income, PayWeek));
        Assert.False(PlanEngine.IsPaycheck(Week[10], PlanBucket.Income, PayWeek));
        Assert.False(PlanEngine.IsPaycheck(Transfer(3, 10_000, PayWeek.Start, AllySavings, UsBank), PlanBucket.Income, PayWeek));
    }

    // ---------------------------------------------------------------------------------------------
    // Savings sweep
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_sweep_is_last_weeks_left_or_nothing()
    {
        Assert.Equal(25_317, PlanEngine.SweepPlanned(25_317));
        Assert.Equal(0, PlanEngine.SweepPlanned(-4_210));
        Assert.Equal(500, PlanEngine.SweepTolerance(2_000));      // $5 floor
        Assert.Equal(1_266, PlanEngine.SweepTolerance(25_317));   // 5 %
    }

    [Fact]
    public void The_sweep_counts_against_this_week_until_it_moves()
    {
        Assert.Equal(234_567, PlanEngine.SweepOut(234_567, done: false));  // − Move to savings
        Assert.Equal(0, PlanEngine.SweepOut(234_567, done: true));         // moved: already out of available
        Assert.Equal(0, PlanEngine.SweepOut(0, done: false));              // nothing to move
    }

    [Fact]
    public void Freezing_late_takes_back_what_already_moved_in_the_new_week()
    {
        // Frozen on payday 2026-11-06 for the week that just ended: only Phone ($85) is still unpaid → left = available − 85.
        var newWeek = PayWeek.Shift(1);
        var previous = new[]
        {
            Bill(1, 185_000, D(2026, 11, 1), UsBank, paid: 185_000, fromBank: true),
            Bill(2, 8_500, D(2026, 11, 3), UsBank),
            Transfer(7, 2_000, D(2026, 10, 30), UsBank, FunTravis, paid: 2_000, fromBank: true),
        };
        Assert.Equal(300_000 - 8_500, PlanEngine.PreviousWeekLeft(newWeek, newWeek.Start, 300_000, previous, UsBank, Roles));

        // The API was down at midnight: by the time it freezes, the paycheck ($1,500) and both $20 transfers posted — take them back.
        var late = previous.Concat(
        [
            Income(10, 150_000, newWeek.Start, UsBank, paid: 150_000, fromBank: true),
            Transfer(7, 2_000, newWeek.Start, UsBank, FunTravis, paid: 2_000, fromBank: true),
            Transfer(8, 2_000, newWeek.Start, UsBank, FunWife, paid: 2_000, fromBank: true),
            Bill(30, 5_000, newWeek.Start, UsBank, paid: 5_000), // marked paid by hand: not a bank movement, no correction
        ]).ToList();
        Assert.Equal(300_000 - 8_500 - 150_000 + 4_000, PlanEngine.PreviousWeekLeft(newWeek, newWeek.Start, 300_000, late, UsBank, Roles));
    }

    private static PlanBankRow Row(int id, Guid? account, DateOnly posted, long cents, string text, Guid? peer = null, Guid? peerAccount = null, bool transferish = false) =>
        new(Id(id), account, posted, cents, peer, peerAccount, transferish, text);

    private static readonly (string, string)[] SavingsNames = [("Ally High Yield Savings", "Ally Bank")];

    [Fact]
    public void The_sweep_is_found_from_a_transfer_pair_to_savings()
    {
        var friday = PayWeek.Start;
        var outflows = new[]
        {
            Row(1, UsBank, friday, -25_317, "ONLINE TRANSFER TO SAV", Id(2), AllySavings),
            Row(3, UsBank, friday, -25_317, "COSTCO WHOLESALE"), // same amount, no transfer evidence
        };
        var match = PlanEngine.FindSweep(25_317, friday, UsBank, [AllySavings], savingsLinked: true, SavingsNames, outflows, []);
        Assert.Equal(new SweepMatch(Id(1), Id(2), 25_317), match);
    }

    [Fact]
    public void The_sweep_pairs_with_a_savings_inflow_and_tolerates_five_percent()
    {
        var friday = PayWeek.Start;
        var outflows = new[] { Row(1, UsBank, friday.AddDays(1), -26_000, "WEB XFER 8812") };
        var inflows = new[] { Row(2, AllySavings, friday.AddDays(2), 26_000, "DEPOSIT") };
        var match = PlanEngine.FindSweep(25_317, friday, UsBank, [AllySavings], savingsLinked: true, SavingsNames, outflows, inflows);
        Assert.Equal(new SweepMatch(Id(1), Id(2), 26_000), match);

        var tooFar = new[] { Row(1, UsBank, friday.AddDays(1), -26_600, "WEB XFER 8812") }; // $12.83 off > 5 % of $253.17
        Assert.Null(PlanEngine.FindSweep(25_317, friday, UsBank, [AllySavings], true, SavingsNames, tooFar, [Row(2, AllySavings, friday, 26_600, "DEPOSIT")]));
    }

    [Theory]
    [InlineData(-1, true)]  // Thursday night is fine
    [InlineData(3, true)]   // Monday
    [InlineData(-2, false)]
    [InlineData(4, false)]
    public void The_sweep_window_is_payday_minus_one_to_plus_three(int days, bool found)
    {
        var friday = PayWeek.Start;
        var outflows = new[] { Row(1, UsBank, friday.AddDays(days), -25_317, "ONLINE TRANSFER TO ALLY BANK") };
        Assert.Equal(found, PlanEngine.FindSweep(25_317, friday, UsBank, [AllySavings], savingsLinked: true, SavingsNames, outflows, []) is not null);
    }

    [Fact]
    public void A_one_sided_sweep_needs_an_unlinked_savings_account_or_its_name()
    {
        var friday = PayWeek.Start;
        var anonymous = new[] { Row(1, UsBank, friday, -25_317, "ONLINE TRANSFER 9931", transferish: true) };
        Assert.Null(PlanEngine.FindSweep(25_317, friday, UsBank, [AllySavings], savingsLinked: true, SavingsNames, anonymous, []));
        Assert.NotNull(PlanEngine.FindSweep(25_317, friday, UsBank, [AllySavings], savingsLinked: false, SavingsNames, anonymous, []));

        var named = new[] { Row(1, UsBank, friday, -25_317, "TRANSFER TO ALLY BANK SAVINGS") };
        Assert.NotNull(PlanEngine.FindSweep(25_317, friday, UsBank, [AllySavings], savingsLinked: true, SavingsNames, named, []));
        Assert.Null(PlanEngine.FindSweep(0, friday, UsBank, [AllySavings], savingsLinked: false, SavingsNames, named, [])); // nothing to move
    }

    // ---------------------------------------------------------------------------------------------
    // Planned transfers
    // ---------------------------------------------------------------------------------------------

    private static readonly PlannedTransfer[] FunTransfers =
    [
        new(Id(7), PayWeek.Start, 2_000, UsBank, FunTravis, true, "Ally Fun (Travis)", "Ally Bank"),
        new(Id(8), PayWeek.Start, 2_000, UsBank, FunWife, true, "Ally Fun (Wife)", "Ally Bank"),
    ];

    [Fact]
    public void Two_identical_twenty_dollar_transfers_pair_with_their_own_fun_accounts()
    {
        var friday = PayWeek.Start;
        var outflows = new[]
        {
            Row(1, UsBank, friday, -2_000, "ONLINE TRANSFER TO ALLY BANK"),
            Row(2, UsBank, friday, -2_000, "ONLINE TRANSFER TO ALLY BANK"),
        };
        var inflows = new[] { Row(11, FunWife, friday, 2_000, "TRANSFER FROM US BANK"), Row(12, FunTravis, friday.AddDays(1), 2_000, "TRANSFER FROM US BANK") };
        var matches = PlanEngine.MatchTransfers(FunTransfers, outflows, inflows);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.ItemId == Id(7) && m.InflowId == Id(12));
        Assert.Contains(matches, m => m.ItemId == Id(8) && m.InflowId == Id(11));
        Assert.Equal(2, matches.Select(m => m.OutflowId).Distinct().Count());
    }

    [Fact]
    public void Planned_transfers_allow_a_dollar_and_two_days()
    {
        var friday = PayWeek.Start;
        var one = new[] { FunTransfers[0] };
        Assert.Single(PlanEngine.MatchTransfers(one, [Row(1, UsBank, friday.AddDays(2), -2_100, "ONLINE TRANSFER TO ALLY BANK")], []));
        Assert.Empty(PlanEngine.MatchTransfers(one, [Row(1, UsBank, friday.AddDays(2), -2_101, "ONLINE TRANSFER TO ALLY BANK")], []));
        Assert.Empty(PlanEngine.MatchTransfers(one, [Row(1, UsBank, friday.AddDays(3), -2_000, "ONLINE TRANSFER TO ALLY BANK")], []));
        Assert.Empty(PlanEngine.MatchTransfers(one, [Row(1, AllySavings, friday, -2_000, "ONLINE TRANSFER TO ALLY BANK")], [])); // wrong from-account
        Assert.Empty(PlanEngine.MatchTransfers(one, [Row(1, UsBank, friday, 2_000, "ONLINE TRANSFER FROM ALLY BANK")], []));   // money in
    }

    [Fact]
    public void A_twenty_dollar_purchase_is_not_fun_money()
    {
        var friday = PayWeek.Start;
        Assert.Empty(PlanEngine.MatchTransfers(FunTransfers, [Row(1, UsBank, friday, -2_000, "CHIPOTLE 1234")], []));

        // … unless the fun account has no bank feed and the row reads like a transfer.
        var unlinked = new[] { FunTransfers[0] with { ToLinked = false, ToName = "Fun (Travis)", ToInstitution = "Credit Union" } };
        Assert.Empty(PlanEngine.MatchTransfers(unlinked, [Row(1, UsBank, friday, -2_000, "CHIPOTLE 1234")], []));
        Assert.Single(PlanEngine.MatchTransfers(unlinked, [Row(1, UsBank, friday, -2_000, "ONLINE TRANSFER 7712", transferish: true)], []));
    }

    [Fact]
    public void An_existing_pair_must_point_at_the_to_account()
    {
        var friday = PayWeek.Start;
        var toSavings = Row(1, UsBank, friday, -2_000, "ONLINE TRANSFER", Id(9), AllySavings);
        Assert.Empty(PlanEngine.MatchTransfers([FunTransfers[0]], [toSavings], []));
        var toTravis = Row(1, UsBank, friday, -2_000, "ONLINE TRANSFER", Id(9), FunTravis);
        Assert.Equal(Id(9), Assert.Single(PlanEngine.MatchTransfers([FunTransfers[0]], [toTravis], [])).InflowId);
    }

    [Theory]
    [InlineData("ONLINE TRANSFER TO ALLY BANK", "Ally Fun", "Ally Bank", true)]
    [InlineData("ALLY BANK $TRANSFER", "Checking", "Ally Bank", true)]
    [InlineData("ONLINE TRANSFER TO SAVINGS", "Ally High Yield Savings", "Ally Bank", false)] // "savings" is generic
    [InlineData("US BANK ONLINE TRANSFER", "Fun", "US Bank", false)]                         // "us"/"bank" never identify an account
    [InlineData("TRANSFER TO DISCOVER SAVINGS 1234", "Discover Online Savings", "Discover", true)]
    [InlineData("", "Ally Fun", "Ally Bank", false)]
    public void Bank_text_names_an_account_by_a_significant_word(string text, string name, string institution, bool expected) =>
        Assert.Equal(expected, PlanEngine.NamesAccount(text, name, institution));

    // ---------------------------------------------------------------------------------------------
    // Payday push
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_payday_push_carries_values_only()
    {
        Assert.Equal("Payday · move $253.17 to savings · fun money $40.00", SchedulerRules.PaydayBody(25_317, 4_000, false, "USD"));
        Assert.Equal("Payday · move $253.17 to savings · fun money $40.00 sent ✓", SchedulerRules.PaydayBody(25_317, 4_000, true, "USD"));
        Assert.Equal("Payday · nothing to move to savings · fun money $40.00", SchedulerRules.PaydayBody(0, 4_000, false, "USD"));
        Assert.Equal("Payday · fun money $340.00", SchedulerRules.PaydayBody(null, 34_000, false, "USD"));
        Assert.Equal("Payday", SchedulerRules.PaydayBody(null, 0, false, "USD"));
        Assert.Equal("payday", SchedulerRules.PaydayTag);
        Assert.Equal("payday", WeekSummaryKind.Payday.ToDb());
    }

    // ---------------------------------------------------------------------------------------------
    // Validators
    // ---------------------------------------------------------------------------------------------

    private static RecurringRequest Rec(Kind kind = Kind.Bill, long amount = 2_000, Guid? from = null, Guid? to = null, string name = "Fun money") =>
        new(name, kind, amount, null, null, Frequency.Weekly, D(2026, 10, 30), null, null, null, null, null, null, null, from, to);

    [Fact]
    public void A_transfer_needs_two_different_accounts()
    {
        var validator = new RecurringRequestValidator();
        Assert.Equal(["fromAccountId", "toAccountId"], Fields(validator, Rec(Kind.Transfer)));
        Assert.Equal(["toAccountId"], Fields(validator, Rec(Kind.Transfer, from: UsBank)));
        Assert.Equal(["toAccountId"], Fields(validator, Rec(Kind.Transfer, from: UsBank, to: UsBank)));
        Assert.Empty(Fields(validator, Rec(Kind.Transfer, from: UsBank, to: FunTravis)));
        Assert.Empty(Fields(validator, Rec(Kind.Bill))); // other kinds ignore from / to
    }

    [Fact]
    public void Bulk_errors_are_per_index()
    {
        var validator = new RecurringBulkRequestValidator();
        var request = new RecurringBulkRequest([Rec(), Rec(), Rec(), Rec(amount: 0), Rec(Kind.Transfer, name: "")]);
        Assert.Equal(["items[3].amountCents", "items[4].fromAccountId", "items[4].name", "items[4].toAccountId"], Fields(validator, request));
        Assert.Equal(["items"], Fields(validator, new RecurringBulkRequest([])));
        Assert.Equal(["items"], Fields(validator, new RecurringBulkRequest(null)));
        Assert.Equal(["items"], Fields(validator, new RecurringBulkRequest(Enumerable.Range(0, 101).Select(_ => Rec()).ToArray())));
        Assert.Empty(Fields(validator, new RecurringBulkRequest(Enumerable.Range(0, 100).Select(_ => Rec()).ToArray())));
    }

    [Fact]
    public void Payday_weekday_is_zero_to_six()
    {
        var validator = new HouseholdSettingsRequestValidator();
        static HouseholdSettingsRequest With(Optional<int?> payday) => new(default, default, default, default, default, default, default, default, default, default, payday);
        Assert.Empty(Fields(validator, With(Optional<int?>.Of(5))));
        Assert.Empty(Fields(validator, With(Optional<int?>.Of(0))));
        Assert.Empty(Fields(validator, With(Optional<int?>.Of(6))));
        Assert.Empty(Fields(validator, With(Optional<int?>.Missing)));
        Assert.Equal(["paydayWeekday"], Fields(validator, With(Optional<int?>.Of(7))));
        Assert.Equal(["paydayWeekday"], Fields(validator, With(Optional<int?>.Of(-1))));
        Assert.Equal(["paydayWeekday"], Fields(validator, With(Optional<int?>.Of(null))));
    }

    [Fact]
    public void Account_role_requests_name_a_role()
    {
        var validator = new AccountRoleRequestValidator();
        Assert.Empty(Fields(validator, new AccountRoleRequest(AccountRole.Fun, Id(1))));
        Assert.Empty(Fields(validator, new AccountRoleRequest(AccountRole.Bills, null)));
        Assert.Equal(["role"], Fields(validator, new AccountRoleRequest(null, null)));
        Assert.Equal(["role"], Fields(validator, new AccountRoleRequest((AccountRole)9, null)));
        Assert.Equal(["ownerUserId"], Fields(validator, new AccountRoleRequest(AccountRole.Fun, Guid.Empty)));
    }

    [Fact]
    public void Sweep_done_needs_the_week()
    {
        var validator = new SweepDoneRequestValidator();
        Assert.Equal(["weekStart"], Fields(validator, new SweepDoneRequest(null, null)));
        Assert.Equal(["actualCents"], Fields(validator, new SweepDoneRequest(PayWeek.Start, -1)));
        Assert.Empty(Fields(validator, new SweepDoneRequest(PayWeek.Start, 25_317)));
    }

    [Fact]
    public void Transfers_are_money_out_but_never_spending()
    {
        Assert.True(Kind.Transfer.IsMoneyOut());
        Assert.False(Kind.Transfer.IsOutflow());
        Assert.True(TransactionMatcher.SignMatchesKind(Kind.Transfer, isOutflow: true));
        Assert.False(TransactionMatcher.SignMatchesKind(Kind.Transfer, isOutflow: false));

        // The v4 week math subtracts a planned transfer from what is left but never counts it as spending, budgets or income.
        var week = new WeekWindow(D(2026, 10, 30), D(2026, 11, 5));
        var occurrences = new[] { new WeekOccurrence(Id(1), Kind.Transfer, 2_000, D(2026, 10, 31), null), new WeekOccurrence(Id(2), Kind.Transfer, 2_000, D(2026, 10, 30), 2_000) };
        var totals = WeekEngine.Totals(week, occurrences, []);
        Assert.Equal(new WeekTotals(0, 0, 0, 0, 0, 0), totals);
        Assert.Equal(0, WeekEngine.SpentCents(week, null, occurrences, []));
        Assert.Equal(10_000 - 2_000, WeekEngine.ProjectedLeft(10_000, week.Start, week.End, occurrences));
    }

    // ---------------------------------------------------------------------------------------------
    // "Only one pending shows": the parser and the reconciler keep every pending transaction the feed sends
    // ---------------------------------------------------------------------------------------------

    /// <summary>Five pending authorizations on one account in every shape the Bridge uses, plus one posted row.</summary>
    public const string FivePendingFeed = """
        {
          "errors": [],
          "accounts": [{
            "org": { "domain": "usbank.com", "name": "U.S. Bank" },
            "id": "ACT-USB-1", "name": "Checking", "currency": "USD",
            "balance": "2408.07", "available-balance": "2345.67", "balance-date": 1793361600,
            "transactions": [
              { "id": "p-walmart-1", "posted": 1793318400, "amount": "-25.00", "description": "WALMART #1234", "pending": true },
              { "id": "p-walmart-2", "posted": 1793318400, "amount": "-25.00", "description": "WALMART #1234", "pending": true },
              { "id": "p-shell-v2",  "posted": 0, "transacted_at": 1793275200, "amount": "-30.00", "description": "SHELL OIL 5741" },
              { "id": "p-sbux-ta",   "transacted_at": 1793232000, "amount": "-7.40", "description": "STARBUCKS 0091", "pending": true },
              { "id": "p-xfer-sav",  "posted": 1793318400, "amount": "-50.00", "description": "ONLINE TRANSFER TO SAVINGS", "pending": true },
              { "id": "t-payroll",   "posted": 1793318400, "amount": "1500.00", "description": "ACME PAYROLL" }
            ]
          }]
        }
        """;

    [Fact]
    public void The_parser_keeps_all_five_pending_transactions()
    {
        var set = SimpleFinParser.Parse(FivePendingFeed, D(2026, 10, 30));
        var account = Assert.Single(set.Accounts);
        Assert.Equal(234_567, account.AvailableCents);
        Assert.Equal(5, account.Transactions.Count(t => t.Pending));
        Assert.Equal(new[] { "p-shell-v2", "p-walmart-1", "p-walmart-2", "p-sbux-ta", "p-xfer-sav" }.Order(), account.Transactions.Where(t => t.Pending).Select(t => t.Id).Order());
        Assert.Equal(D(2026, 10, 29), account.Transactions.Single(t => t.Id == "p-shell-v2").PostedOn); // posted = 0 → transacted_at
        Assert.Equal(D(2026, 10, 29), account.Transactions.Single(t => t.Id == "p-sbux-ta").PostedOn);  // no posted at all → transacted_at
        Assert.Equal(13_740, account.Transactions.Where(t => t.Pending).Sum(t => -t.AmountCents)); // 25 + 25 + 30 + 7.40 + 50

        // Distinct ids → distinct fingerprints: two same-merchant same-amount authorizations never collapse.
        var fingerprints = account.Transactions.Select(t => Convert.ToHexString(Fingerprint.ForExternal("simplefin", account.Id, t.Id))).ToList();
        Assert.Equal(fingerprints.Count, fingerprints.Distinct().Count());
    }

    [Fact]
    public void The_reconciler_inserts_every_pending_row_and_never_pairs_two_pendings()
    {
        var set = SimpleFinParser.Parse(FivePendingFeed, D(2026, 10, 30));
        var incoming = set.Accounts[0].Transactions.Select(t => new ReconcileIncoming(t.Id, t.Pending, t.PostedOn, t.AmountCents, t.Description, t.Payee)).ToList();

        var first = PendingReconciler.Reconcile([], incoming, D(2026, 10, 1), D(2026, 10, 30), judgeAbsence: true);
        Assert.Equal(6, first.New.Count);
        Assert.Empty(first.Replacements);

        // Second sync: everything stored and still pending → all same-id, nothing dropped / superseded / replaced.
        var stored = first.New.Select((t, i) => new ReconcileExisting(Id(100 + i), t.ExternalId, t.Pending, t.PostedOn, t.AmountCents, t.Description, t.Payee, "pending", null, new DateTime(2026, 10, 30, 12, 0, i, DateTimeKind.Utc))).ToList();
        var second = PendingReconciler.Reconcile(stored, incoming, D(2026, 10, 1), D(2026, 10, 30), judgeAbsence: true);
        Assert.Equal(6, second.SameId.Count);
        Assert.Empty(second.New);
        Assert.Empty(second.Dropped);
        Assert.Empty(second.Superseded);
        Assert.Empty(second.Replacements);
    }
}
