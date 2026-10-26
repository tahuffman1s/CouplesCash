using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class PendingReconcilerTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static readonly DateOnly WindowStart = Today.AddDays(-30);
    private static readonly DateTime Created = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static int seq;

    private static ReconcileExisting Stored(
        string? externalId, bool pending, DateOnly postedOn, long cents, string description,
        string status = "pending", string? ignoreReason = null, string? payee = null, int createdMinutes = 0) =>
        new(Guid.CreateVersion7(), externalId, pending, postedOn, cents, description, payee, status, ignoreReason,
            Created.AddMinutes(createdMinutes == 0 ? Interlocked.Increment(ref seq) : createdMinutes));

    private static ReconcileIncoming In(string externalId, bool pending, DateOnly postedOn, long cents, string description, string? payee = null) =>
        new(externalId, pending, postedOn, cents, description, payee);

    private static ReconcileResult Run(IReadOnlyList<ReconcileExisting> existing, IReadOnlyList<ReconcileIncoming> incoming, bool judgeAbsence = true) =>
        PendingReconciler.Reconcile(existing, incoming, WindowStart, Today, judgeAbsence);

    // ---------------------------------------------------------------------------------------------
    // Same id
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Same_id_that_posts_is_refreshed_in_place()
    {
        var pending = Stored("P1", true, Today.AddDays(-1), -4000, "THE DINER");

        var result = Run([pending], [In("P1", false, Today, -4000, "THE DINER")]);

        var same = Assert.Single(result.SameId);
        Assert.Equal(pending.Id, same.ExistingId);
        Assert.True(same.Posts);
        Assert.False(same.AmountChanged);
        Assert.Empty(result.New);
        Assert.Empty(result.Replacements);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void Same_id_that_posts_with_a_tip_reports_the_amount_change()
    {
        var pending = Stored("P1", true, Today.AddDays(-1), -4000, "THE DINER");

        var same = Assert.Single(Run([pending], [In("P1", false, Today, -4800, "THE DINER")]).SameId);

        Assert.True(same.Posts);
        Assert.True(same.AmountChanged);
    }

    [Fact]
    public void Same_id_still_pending_is_not_a_post()
    {
        var pending = Stored("P1", true, Today.AddDays(-1), -4000, "THE DINER");

        var same = Assert.Single(Run([pending], [In("P1", true, Today.AddDays(-1), -4000, "THE DINER")]).SameId);

        Assert.False(same.Posts);
        Assert.False(same.AmountChanged);
    }

    [Fact]
    public void A_repeated_id_in_one_response_counts_once()
    {
        var result = Run([], [In("X", false, Today, -100, "A"), In("X", false, Today, -100, "A")]);

        Assert.Single(result.New);
    }

    // ---------------------------------------------------------------------------------------------
    // New posted id replaces the absent pending row
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void New_posted_id_with_the_same_amount_replaces_the_absent_pending_row()
    {
        var pending = Stored("P1", true, Today.AddDays(-2), -2599, "AMAZON MKTPL*AB12");

        var result = Run([pending], [In("D1", false, Today, -2599, "AMAZON MKTPL*ZZ99")]);

        var replacement = Assert.Single(result.Replacements);
        Assert.Equal(pending.Id, replacement.ExistingId);
        Assert.Equal("D1", replacement.Incoming.ExternalId);
        Assert.False(replacement.AmountChanged);
        Assert.Empty(result.New);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void Restaurant_tip_of_twenty_percent_replaces_in_place_with_an_amount_change()
    {
        var pending = Stored("P1", true, Today.AddDays(-1), -4000, "THE DINER");

        var result = Run([pending], [In("D1", false, Today, -4800, "THE DINER")]);

        var replacement = Assert.Single(result.Replacements);
        Assert.Equal(pending.Id, replacement.ExistingId);
        Assert.True(replacement.AmountChanged);
        Assert.Equal(-4800, replacement.Incoming.AmountCents);
        Assert.Empty(result.New);
    }

    [Fact]
    public void More_than_twenty_five_percent_above_the_floor_is_a_new_row()
    {
        // $40.00 → $52.00 is 30 % (tolerance max(25 %, $5) = $10.00).
        var pending = Stored("P1", true, Today.AddDays(-1), -4000, "THE DINER");

        var result = Run([pending], [In("D1", false, Today, -5200, "THE DINER")]);

        Assert.Empty(result.Replacements);
        Assert.Single(result.New);
        Assert.Empty(result.Dropped); // only a day old
    }

    [Fact]
    public void Small_amounts_use_the_five_dollar_floor()
    {
        // $3.00 → $7.50: 150 % but within $5.00.
        var pending = Stored("P1", true, Today.AddDays(-1), -300, "PARKING METER");

        Assert.Single(Run([pending], [In("D1", false, Today, -750, "PARKING METER")]).Replacements);
        Assert.Equal(500, PendingReconciler.Tolerance(-300));
        Assert.Equal(1000, PendingReconciler.Tolerance(-4000));
        Assert.Equal(2500, PendingReconciler.Tolerance(10000));
    }

    [Fact]
    public void Fuel_hold_of_one_dollar_does_not_match_the_posted_fill_up_and_is_later_dropped()
    {
        var hold = Stored("H1", true, Today.AddDays(-7), -100, "SHELL OIL 12345");
        var fill = In("F1", false, Today.AddDays(-6), -4512, "SHELL OIL 12345");

        var early = PendingReconciler.Reconcile([hold], [fill], WindowStart, Today.AddDays(-5), judgeAbsence: true);
        Assert.Empty(early.Replacements);
        Assert.Single(early.New);
        Assert.Empty(early.Dropped); // two days old: give the bank time

        var later = Run([hold], [fill]);
        Assert.Empty(later.Replacements);
        Assert.Equal([hold.Id], later.Dropped);
    }

    [Fact]
    public void Two_similar_pendings_pick_the_closest_and_the_other_stays()
    {
        var older = Stored("P1", true, Today.AddDays(-3), -500, "STARBUCKS STORE 1");
        var closer = Stored("P2", true, Today.AddDays(-1), -600, "STARBUCKS STORE 1");

        var result = Run([older, closer], [In("D1", false, Today, -600, "STARBUCKS STORE 1")]);

        var replacement = Assert.Single(result.Replacements);
        Assert.Equal(closer.Id, replacement.ExistingId);
        Assert.Empty(result.New);
        Assert.Empty(result.Dropped); // the other one is only 3 days old
    }

    [Fact]
    public void Each_pending_row_and_each_posted_transaction_is_used_once()
    {
        var a = Stored("P1", true, Today.AddDays(-2), -1000, "UBER TRIP");
        var b = Stored("P2", true, Today.AddDays(-2), -1500, "UBER TRIP");

        var result = Run([a, b], [In("D1", false, Today, -1000, "UBER TRIP"), In("D2", false, Today, -1500, "UBER TRIP"), In("D3", false, Today, -1200, "UBER TRIP")]);

        Assert.Equal(2, result.Replacements.Count);
        Assert.Equal("D1", result.Replacements.Single(r => r.ExistingId == a.Id).Incoming.ExternalId);
        Assert.Equal("D2", result.Replacements.Single(r => r.ExistingId == b.Id).Incoming.ExternalId);
        Assert.Equal("D3", Assert.Single(result.New).ExternalId);
    }

    [Fact]
    public void Posted_date_must_be_within_one_day_before_and_seven_days_after()
    {
        var pending = Stored("P1", true, Today.AddDays(-8), -4000, "HOTEL DEPOSIT");

        Assert.Empty(Run([pending], [In("D1", false, Today.AddDays(-10), -4000, "HOTEL DEPOSIT")], judgeAbsence: false).Replacements);
        Assert.Single(Run([pending], [In("D1", false, Today.AddDays(-9), -4000, "HOTEL DEPOSIT")], judgeAbsence: false).Replacements);
        Assert.Single(Run([pending], [In("D1", false, Today.AddDays(-1), -4000, "HOTEL DEPOSIT")], judgeAbsence: false).Replacements);
        Assert.Empty(Run([pending], [In("D1", false, Today, -4000, "HOTEL DEPOSIT")], judgeAbsence: false).Replacements);
    }

    [Fact]
    public void A_refund_never_replaces_a_purchase_and_unrelated_descriptions_never_match()
    {
        var pending = Stored("P1", true, Today.AddDays(-1), -4000, "THE DINER");

        Assert.Empty(Run([pending], [In("R1", false, Today, 4000, "THE DINER")]).Replacements);
        Assert.Empty(Run([pending], [In("S1", false, Today, -4000, "SHELL OIL")]).Replacements);
    }

    [Fact]
    public void Description_similarity_uses_token_overlap_first_token_or_payee()
    {
        Assert.Equal(1.0, PendingReconciler.Similarity("SQ *BLUE BOTTLE COFFEE", null, "BLUE BOTTLE COFFEE #12", null));
        Assert.Equal(0.5, PendingReconciler.Similarity("AMAZON MKTPL*AB12", null, "AMAZON.COM*XY99 AMZN.COM/BILL", null));
        Assert.Equal(1.0, PendingReconciler.Similarity("POS 1234", "Kroger", "DEBIT 9876", "KROGER"));
        Assert.Null(PendingReconciler.Similarity("SHELL OIL", null, "THE DINER", null));
        Assert.Null(PendingReconciler.Similarity("", null, "THE DINER", null));
    }

    // ---------------------------------------------------------------------------------------------
    // Dropped authorizations
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Absent_pending_older_than_five_days_inside_the_window_is_dropped()
    {
        var stale = Stored("P1", true, Today.AddDays(-6), -2500, "HOTEL HOLD");
        var fiveDays = Stored("P2", true, Today.AddDays(-5), -2500, "PARKING");

        var result = Run([stale, fiveDays], []);

        Assert.Equal([stale.Id], result.Dropped);
    }

    [Fact]
    public void Absent_pending_outside_the_fetch_window_is_not_dropped()
    {
        var old = Stored("P1", true, WindowStart.AddDays(-1), -2500, "HOTEL HOLD");

        Assert.Empty(Run([old], []).Dropped);
    }

    [Fact]
    public void Absence_is_not_judged_when_the_response_was_not_clean()
    {
        var stale = Stored("P1", true, Today.AddDays(-10), -2500, "HOTEL HOLD");
        var pending = Stored("P2", true, Today.AddDays(-1), -4000, "THE DINER");

        var result = Run([stale, pending], [In("D1", false, Today, -4800, "THE DINER")], judgeAbsence: false);

        Assert.Empty(result.Dropped);
        Assert.Single(result.Replacements); // a matching posted row is still positive evidence
    }

    [Fact]
    public void A_pending_row_still_in_the_response_is_never_dropped_or_replaced()
    {
        var pending = Stored("P1", true, Today.AddDays(-9), -4000, "THE DINER");

        var result = Run([pending], [In("P1", true, Today.AddDays(-9), -4000, "THE DINER"), In("D1", false, Today.AddDays(-8), -4000, "THE DINER")]);

        Assert.Empty(result.Dropped);
        Assert.Empty(result.Replacements);
        Assert.Single(result.New);
    }

    [Fact]
    public void Hidden_rows_are_neither_dropped_again_nor_replaced()
    {
        var dropped = Stored("P1", true, Today.AddDays(-9), -4000, "THE DINER", status: "ignored", ignoreReason: PendingReconciler.Dropped);

        var result = Run([dropped], [In("D1", false, Today.AddDays(-8), -4000, "THE DINER")]);

        Assert.Empty(result.Dropped);
        Assert.Empty(result.Replacements);
        Assert.Single(result.New);
    }

    [Fact]
    public void A_confirmed_pending_row_keeps_its_review_when_replaced()
    {
        var confirmed = Stored("P1", true, Today.AddDays(-1), -4000, "THE DINER", status: "confirmed");

        var replacement = Assert.Single(Run([confirmed], [In("D1", false, Today, -4800, "THE DINER")]).Replacements);

        Assert.Equal(confirmed.Id, replacement.ExistingId);
    }

    // ---------------------------------------------------------------------------------------------
    // Superseded (pre-v5 duplicates: the posted version was already stored as its own row)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_untouched_authorization_whose_posted_row_exists_is_superseded()
    {
        var pending = Stored("P1", true, Today.AddDays(-3), -4000, "THE DINER", createdMinutes: 10);
        var posted = Stored("D1", false, Today.AddDays(-2), -4800, "THE DINER", createdMinutes: 20);

        var result = Run([pending, posted], [In("D1", false, Today.AddDays(-2), -4800, "THE DINER")]);

        var pair = Assert.Single(result.Superseded);
        Assert.Equal(pending.Id, pair.HiddenId);
        Assert.Equal(posted.Id, pair.SurvivorId);
        Assert.False(pair.ApplyToSurvivor);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void A_reviewed_authorization_absorbs_its_untouched_posted_duplicate()
    {
        var pending = Stored("P1", true, Today.AddDays(-3), -4000, "THE DINER", status: "confirmed", createdMinutes: 10);
        var posted = Stored("D1", false, Today.AddDays(-2), -4800, "THE DINER", createdMinutes: 20);

        var pair = Assert.Single(Run([pending, posted], [In("D1", false, Today.AddDays(-2), -4800, "THE DINER")]).Superseded);

        Assert.Equal(posted.Id, pair.HiddenId);
        Assert.Equal(pending.Id, pair.SurvivorId);
        Assert.True(pair.ApplyToSurvivor);
        Assert.Equal(-4800, pair.Posted.AmountCents);
    }

    [Fact]
    public void When_both_copies_were_reviewed_neither_is_hidden_nor_dropped()
    {
        var pending = Stored("P1", true, Today.AddDays(-9), -4000, "THE DINER", status: "confirmed", createdMinutes: 10);
        var posted = Stored("D1", false, Today.AddDays(-8), -4800, "THE DINER", status: "confirmed", createdMinutes: 20);

        var result = Run([pending, posted], [In("D1", false, Today.AddDays(-8), -4800, "THE DINER")]);

        Assert.Empty(result.Superseded);
        Assert.Empty(result.Dropped);
    }
}
