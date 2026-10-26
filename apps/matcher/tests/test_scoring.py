from __future__ import annotations

import random
from datetime import date, timedelta
from typing import Any

import pytest

from factories import cand, match_request, rule, tx
from matcher.models import MatchRequest, MatchResponse
from matcher.scoring import (
    TOL_ONCE,
    TOL_RECURRING,
    TOL_VARIES,
    amount_score,
    best_name_score,
    combined_score,
    date_score,
    tolerance_for,
)
from matcher.scoring import match as run_match

RENT = ["Rent", "OAKWOOD PROPERTY"]


def run(payload: dict[str, Any]) -> MatchResponse:
    return run_match(MatchRequest.model_validate(payload))


def only_match(payload: dict[str, Any]):
    res = run(payload)
    assert len(res.matches) == 1, res.matches
    return res.matches[0]


# --------------------------------------------------------------------------- component scores


def test_amount_score_exact_inside_outside() -> None:
    assert amount_score(185000, 185000, TOL_RECURRING) == (100, 0, 3700)
    # inside: 1850 of 3700 → halfway between 100 and 70
    assert amount_score(186850, 185000, TOL_RECURRING)[0] == 85
    # small bills: the $1 minimum tolerance applies ($10.30 vs $10.00 is inside)
    assert amount_score(1030, 1000, TOL_RECURRING)[0] == 91
    # outside: $12.50 vs $10.00 → excess $1.50 = 15 % → 70 − 60 = 10; at 20 % excess the score bottoms out
    assert amount_score(1250, 1000, TOL_RECURRING)[0] == 10
    assert amount_score(1300, 1000, TOL_RECURRING)[0] == 0
    # outside by 1 % → 66
    assert amount_score(10300, 10000, TOL_RECURRING)[0] == 66


def test_tolerances_once_and_amount_varies() -> None:
    assert tolerance_for("monthly", False) is TOL_RECURRING
    assert tolerance_for("once", False) is TOL_ONCE
    assert tolerance_for("once", True) is TOL_VARIES
    # one-off oil change: $60 planned, $54.20 paid → $5.80 inside max(15 %, $10)
    assert amount_score(5420, 6000, TOL_ONCE)[0] == 83
    # same pair as a recurring bill is far outside max(2 %, $1)
    assert amount_score(5420, 6000, TOL_RECURRING)[0] == 39
    # utility $90 expected, $112.50 billed → inside max(35 %, $15)
    assert amount_score(11250, 9000, TOL_VARIES)[0] == 79


def test_date_score_window_and_symmetry() -> None:
    assert date_score(0, TOL_RECURRING) == 100
    assert date_score(5, TOL_RECURRING) == 60
    assert date_score(-5, TOL_RECURRING) == 60
    assert date_score(6, TOL_RECURRING) is None
    assert date_score(-7, TOL_ONCE) == 60
    assert date_score(10, TOL_VARIES) == 60
    assert date_score(-2, TOL_RECURRING) == date_score(2, TOL_RECURRING) == 84


def test_combined_weights() -> None:
    assert combined_score(100, 100, 100) == 100
    assert combined_score(91, 100, 82) == 92  # 40.95 + 35 + 16.4 = 92.35
    assert combined_score(0, 0, 0) == 0


# --------------------------------------------------------------------------- realistic matches


def test_rent_ach_matches_rent_with_bank_name() -> None:
    m = only_match(
        match_request(
            [tx("2026-10-30", -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT RENT", id="t-rent")],
            [cand("rent", "2026-11-01", 185000, RENT)],
        )
    )
    assert (m.transaction_id, m.item_id, str(m.due_date)) == ("t-rent", "rent", "2026-11-01")
    assert (m.name_score, m.amount_score, m.date_score, m.score) == (100, 100, 84, 97)
    assert m.auto is True
    assert m.reason == "name 100% · amount exact · 2 days early"


def test_rent_matches_on_item_name_alone() -> None:
    m = only_match(
        match_request(
            [tx("2026-11-01", -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT RENT")],
            [cand("rent", "2026-11-01", 185000, ["Rent"])],
        )
    )
    assert m.name_score >= 95 and m.auto


def test_netflix_descriptor_with_phone_and_state() -> None:
    m = only_match(
        match_request(
            [tx("2026-10-07", -1549, "NETFLIX.COM 866-579-7172 CA")],
            [cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription")],
        )
    )
    assert (m.score, m.auto, m.reason) == (100, True, "name 100% · amount exact · on the due date")


def test_paypal_spotify_descriptor() -> None:
    m = only_match(
        match_request(
            [tx("2026-10-13", -1199, "PAYPAL *SPOTIFYUSAI")],
            [cand("sp", "2026-10-12", 1199, ["Spotify"], kind="subscription")],
        )
    )
    assert m.name_score == 100 and m.auto
    assert m.reason.endswith("1 day late")


def test_state_farm_learned_bank_name() -> None:
    m = only_match(
        match_request(
            [tx("2026-10-15", -21600, "STATE FARM INSURANCE 800-956-6310")],
            [cand("ins", "2026-10-15", 21600, ["Car insurance", "State Farm"])],
        )
    )
    assert m.name_score == 100 and m.auto


def test_utility_amount_varies_widens_amount_and_date() -> None:
    payload = match_request(
        [tx("2026-10-28", -11250, "CITY OF AUSTIN UTIL WEB PMT")],
        [cand("util", "2026-10-20", 9000, ["Water & power", "CITY OF AUSTIN UTIL"], amountVaries=True)],
    )
    m = only_match(payload)
    assert (m.name_score, m.amount_score, m.date_score, m.score) == (100, 79, 68, 86)
    assert m.auto
    # Without amountVaries the same payment is 8 days late: outside ±5, the pair is skipped.
    payload["candidates"][0]["amountVaries"] = False
    assert run(payload).matches == []


def test_one_off_oil_change_amount_differs() -> None:
    m = only_match(
        match_request(
            [tx("2026-10-26", -5420, "JIFFY LUBE #1234")],
            [cand("oil", "2026-10-24", 6000, ["Oil change", "JIFFY LUBE"], frequency="once")],
        )
    )
    assert (m.amount_score, m.date_score, m.score, m.auto) == (83, 89, 92, True)
    assert m.reason == "name 100% · amount off by $5.80 · 2 days late"


def test_payroll_income() -> None:
    m = only_match(
        match_request(
            [tx("2026-10-23", 320000, "ACME CORP PAYROLL PPD ID: 123")],
            [
                cand(
                    "pay",
                    "2026-10-23",
                    320000,
                    ["Paycheck", "ACME CORP"],
                    kind="income",
                    frequency="biweekly",
                )
            ],
        )
    )
    assert m.item_id == "pay" and m.auto


def test_pending_transactions_are_matched_like_posted() -> None:
    m = only_match(
        match_request(
            [tx("2026-10-07", -1549, "NETFLIX.COM", pending=True)],
            [cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription")],
        )
    )
    assert m.auto


def test_payee_is_scored_too() -> None:
    m = only_match(
        match_request(
            [tx("2026-10-07", -4500, "POS DEBIT 0012345678", payee="Planet Fitness")],
            [cand("gym", "2026-10-07", 4500, ["Planet Fitness"])],
        )
    )
    assert m.name_score == 100


# --------------------------------------------------------------------------- skips


def test_sign_mismatch_is_skipped() -> None:
    res = run(
        match_request(
            [
                tx("2026-10-23", -320000, "ACME CORP PAYROLL"),  # outflow vs income
                tx("2026-10-07", 1549, "NETFLIX.COM REFUND"),  # inflow vs subscription
            ],
            [
                cand("pay", "2026-10-23", 320000, ["ACME CORP"], kind="income"),
                cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription"),
            ],
        )
    )
    assert res.matches == []


def test_outside_date_window_is_skipped() -> None:
    res = run(
        match_request(
            [tx("2026-10-13", -1549, "NETFLIX.COM")],
            [cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription")],
        )
    )
    assert res.matches == []


def test_one_off_window_is_seven_days() -> None:
    payload = match_request(
        [tx("2026-10-31", -6000, "JIFFY LUBE")],
        [cand("oil", "2026-10-24", 6000, ["Jiffy Lube"], frequency="once")],
    )
    assert only_match(payload).date_score == 60
    payload["transactions"][0]["postedOn"] = "2026-11-01"
    assert run(payload).matches == []


def test_below_suggest_threshold_not_returned() -> None:
    payload = match_request(
        [tx("2026-10-12", -4999, "TOTALLY DIFFERENT MERCHANT")],
        [cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription")],
    )
    assert run(payload).matches == []


def test_zero_amount_transaction_ignored() -> None:
    assert (
        run(
            match_request([tx("2026-10-07", 0, "NETFLIX")], [cand("nf", "2026-10-07", 0, ["Netflix"])])
        ).matches
        == []
    )


# --------------------------------------------------------------------------- assignment


def test_two_candidates_greedy_by_name() -> None:
    res = run(
        match_request(
            [
                tx("2026-10-07", -1549, "NETFLIX.COM", id="a"),
                tx("2026-10-07", -1549, "HULU 877-8244858 CA", id="b"),
            ],
            [
                cand("hulu", "2026-10-07", 1549, ["Hulu"], kind="subscription"),
                cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription"),
            ],
        )
    )
    assert {(m.transaction_id, m.item_id) for m in res.matches} == {("a", "nf"), ("b", "hulu")}
    # Two candidates with the same exact amount in the window: the exact-amount auto rule does not fire,
    # but the strong name keeps them automatic through the threshold rule.
    assert all(m.auto for m in res.matches)


def test_two_same_amount_transactions_one_to_one() -> None:
    res = run(
        match_request(
            [
                tx("2026-10-09", -1549, "NETFLIX.COM", id="late"),
                tx("2026-10-07", -1549, "NETFLIX.COM", id="ontime"),
            ],
            [cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription")],
        )
    )
    assert [(m.transaction_id, m.item_id) for m in res.matches] == [("ontime", "nf")]


def test_two_occurrences_two_transactions() -> None:
    res = run(
        match_request(
            [
                tx("2026-10-02", -4500, "PLANET FITNESS", id="w1"),
                tx("2026-10-09", -4500, "PLANET FITNESS", id="w2"),
            ],
            [
                cand("gym", "2026-10-01", 4500, ["Gym", "Planet Fitness"], frequency="weekly"),
                cand("gym", "2026-10-08", 4500, ["Gym", "Planet Fitness"], frequency="weekly"),
            ],
        )
    )
    assert {(m.transaction_id, str(m.due_date)) for m in res.matches} == {
        ("w1", "2026-10-01"),
        ("w2", "2026-10-08"),
    }


def test_paid_candidate_never_assigned() -> None:
    res = run(
        match_request(
            [tx("2026-10-07", -1549, "NETFLIX.COM")],
            [cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription", paid=True)],
        )
    )
    assert res.matches == []


def test_same_account_breaks_ties() -> None:
    res = run(
        match_request(
            [tx("2026-10-07", -5000, "CHECK", accountId="acct-2")],
            [
                cand("a", "2026-10-07", 5000, ["Check"], accountId="acct-1"),
                cand("b", "2026-10-07", 5000, ["Check"], accountId="acct-2"),
            ],
        )
    )
    assert res.matches[0].item_id == "b"


# --------------------------------------------------------------------------- auto rules


HONDA = ["Honda car loan"]


def test_auto_by_exact_amount_branch() -> None:
    # name 62 → combined 83 < 85, but exact amount, on time, name ≥ 50 and the only $350.00 candidate
    m = only_match(
        match_request(
            [tx("2026-10-15", -35000, "HONDA FINANCIAL")], [cand("car", "2026-10-15", 35000, HONDA)]
        )
    )
    assert (m.name_score, m.score, m.auto) == (62, 83, True)
    assert m.reason.endswith("only item with this amount")


def test_exact_branch_needs_unique_amount() -> None:
    res = run(
        match_request(
            [tx("2026-10-15", -35000, "HONDA FINANCIAL")],
            [
                cand("car", "2026-10-15", 35000, HONDA),
                cand("daycare", "2026-10-16", 35000, ["Little Sprouts"]),
            ],
        )
    )
    assert [(m.item_id, m.auto) for m in res.matches] == [("car", False)]


def test_exact_branch_counts_paid_candidates_as_ambiguous() -> None:
    res = run(
        match_request(
            [tx("2026-10-15", -35000, "HONDA FINANCIAL")],
            [
                cand("car", "2026-10-15", 35000, HONDA),
                cand("daycare", "2026-10-14", 35000, ["Daycare"], paid=True),
            ],
        )
    )
    assert [(m.item_id, m.auto) for m in res.matches] == [("car", False)]


def test_exact_branch_needs_three_days_or_less() -> None:
    m = only_match(
        match_request(
            [tx("2026-10-19", -35000, "HONDA FINANCIAL")], [cand("car", "2026-10-15", 35000, HONDA)]
        )
    )
    assert m.auto is False


def test_threshold_branch_needs_amount_inside_tolerance() -> None:
    # name 100, date 100, amount 1 % beyond tolerance (66) → combined 88 ≥ 85 but not automatic
    m = only_match(
        match_request([tx("2026-10-05", -10300, "XFINITY")], [cand("net", "2026-10-05", 10000, ["Xfinity"])])
    )
    assert (m.score, m.amount_score, m.auto) == (88, 66, False)
    assert "amount off by $3.00 (3%)" in m.reason


def test_threshold_branch_needs_name_70() -> None:
    # name 62, amount inside tolerance but not exact → combined 82 ≥ lowered threshold 80, yet not auto
    m = only_match(
        match_request(
            [tx("2026-10-15", -35050, "HONDA FINANCIAL")],
            [cand("car", "2026-10-15", 35000, HONDA)],
            auto_threshold=80,
        )
    )
    assert (m.name_score, m.amount_score, m.score, m.auto) == (62, 98, 82, False)


@pytest.mark.parametrize(("level", "expected"), [("off", False), ("bills", True), ("all", True)])
def test_auto_level_for_occurrences(level: str, expected: bool) -> None:
    m = only_match(
        match_request(
            [tx("2026-10-07", -1549, "NETFLIX.COM")],
            [cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription")],
            auto_level=level,
        )
    )
    assert m.auto is expected


# --------------------------------------------------------------------------- categories


GROCERIES = "cat-groceries"


@pytest.mark.parametrize(("level", "expected"), [("off", False), ("bills", False), ("all", True)])
def test_category_auto_only_at_level_all(level: str, expected: bool) -> None:
    res = run(
        match_request(
            [tx("2026-10-20", -8312, "POS PURCHASE KROGER #0456 XXXX1234 10/20")],
            rules=[rule("KROGER", GROCERIES, 3)],
            auto_level=level,
        )
    )
    assert len(res.categories) == 1
    c = res.categories[0]
    assert (c.category_id, c.score, c.auto) == (GROCERIES, 100, expected)
    assert c.reason == "payee KROGER (3 confirmations)"


def test_category_needs_two_hits_for_auto() -> None:
    res = run(
        match_request(
            [tx("2026-10-20", -2312, "SQ *BLUE BOTTLE COFFEE OAKLAND CA")],
            rules=[rule("Blue Bottle", "c", 1)],
        )
    )
    assert [(c.auto, c.reason) for c in res.categories] == [(False, "payee Blue Bottle (1 confirmation)")]


def test_category_suggestion_between_75_and_90() -> None:
    res = run(
        match_request([tx("2026-10-20", -650, "STARBUCKS")], rules=[rule("STARBUKS COFFEE", "coffee", 5)])
    )
    assert [(c.score, c.auto) for c in res.categories] == [(89, False)]


def test_category_below_75_is_dropped() -> None:
    res = run(match_request([tx("2026-10-20", -4500, "HOME DEPOT")], rules=[rule("HOME GOODS", "home", 5)]))
    assert res.categories == []


def test_category_amazon_alias() -> None:
    res = run(
        match_request([tx("2026-10-20", -2599, "AMZN Mktp US*2K4H71")], rules=[rule("Amazon", "shopping", 4)])
    )
    assert [(c.category_id, c.auto) for c in res.categories] == [("shopping", True)]


def test_category_best_rule_wins() -> None:
    res = run(
        match_request(
            [tx("2026-10-20", -8312, "KROGER #0456")],
            rules=[rule("KROGER FUEL", "fuel", 2), rule("KROGER", GROCERIES, 7), rule("SAFEWAY", "x", 9)],
        )
    )
    # KROGER FUEL also scores 100 (token subset) → tie broken by more confirmations
    assert res.categories[0].category_id == GROCERIES


def test_matched_transactions_are_not_categorised() -> None:
    res = run(
        match_request(
            [
                tx("2026-10-07", -1549, "NETFLIX.COM", id="nf-tx"),
                tx("2026-10-20", -8312, "KROGER", id="k-tx"),
            ],
            [cand("nf", "2026-10-07", 1549, ["Netflix"], kind="subscription")],
            rules=[rule("NETFLIX", "fun", 9), rule("KROGER", GROCERIES, 9)],
        )
    )
    assert [m.transaction_id for m in res.matches] == ["nf-tx"]
    assert [c.transaction_id for c in res.categories] == ["k-tx"]


def test_inflows_are_not_categorised() -> None:
    res = run(match_request([tx("2026-10-20", 8312, "KROGER REFUND")], rules=[rule("KROGER", GROCERIES, 9)]))
    assert res.categories == []


def test_response_engine_and_version() -> None:
    res = run(match_request([]))
    assert res.engine == "fuzzy" and res.version == "1.0.0"
    assert res.matches == [] and res.categories == []


def test_batch_equals_component_functions() -> None:
    """The batch loop inlines the component math for speed; it must agree with the reference functions."""
    rng = random.Random(7)
    for i in range(300):
        expected = rng.choice([500, 1549, 9000, 35000, 185000])
        actual = max(1, expected + rng.randint(-expected // 2, expected // 2))
        days = rng.randint(-10, 10)
        frequency = rng.choice(["monthly", "once", "weekly"])
        varies = rng.random() < 0.3
        tol = tolerance_for(frequency, varies)
        due = date(2026, 10, 15)
        payload = match_request(
            [tx(str(due + timedelta(days=days)), -actual, "SOME MERCHANT 1234")],
            [cand(f"i{i}", str(due), expected, ["Some merchant"], frequency=frequency, amountVaries=varies)],
            suggest_threshold=0,
            auto_level="off",
        )
        res = run(payload)
        d = date_score(days, tol)
        if d is None:
            assert res.matches == []
            continue
        a = amount_score(actual, expected, tol)[0]
        n = best_name_score(("SOME MERCHANT",), ("SOME MERCHANT",))
        [m] = res.matches
        assert (m.amount_score, m.date_score, m.name_score, m.score) == (a, d, n, combined_score(n, a, d))


def test_many_identical_candidates_still_assign_one_to_one() -> None:
    # 40 interchangeable occurrences (far beyond the per-transaction cap of alternatives), 3 transactions.
    candidates = [cand(f"i{i:02d}", "2026-10-07", 1549, ["Netflix"], kind="subscription") for i in range(40)]
    transactions = [tx("2026-10-07", -1549, "NETFLIX.COM", id=f"t{i}") for i in range(3)]
    res = run(match_request(transactions, candidates))
    assert [(m.transaction_id, m.item_id) for m in res.matches] == [
        ("t0", "i00"),
        ("t1", "i01"),
        ("t2", "i02"),
    ]
    assert not any(m.auto for m in res.matches) or all(m.reason.startswith("name 100%") for m in res.matches)
