from __future__ import annotations

from datetime import date, timedelta
from typing import Any

import pytest

from matcher.models import DetectRequest, DetectResponse
from matcher.recurring import add_months, detect_recurring, kind_for, title_case

TODAY = "2026-10-26"


def t(
    on: str, amount: int, description: str, payee: str | None = None, id: str | None = None
) -> dict[str, Any]:
    return {
        "id": id or f"{description[:8]}-{on}",
        "postedOn": on,
        "amountCents": amount,
        "description": description,
        "payee": payee,
    }


def detect(
    transactions: list[dict[str, Any]], known: list[str] | None = None, today: str = TODAY
) -> DetectResponse:
    return detect_recurring(
        DetectRequest.model_validate(
            {"today": today, "transactions": transactions, "knownNames": known or []}
        )
    )


def monthly(
    day: int, months: list[int], amount: int, description: str, year: int = 2026
) -> list[dict[str, Any]]:
    return [t(f"{year}-{m:02d}-{day:02d}", amount, description) for m in months]


NETFLIX = monthly(7, [7, 8, 9, 10], -1549, "NETFLIX.COM 866-579-7172 CA")


def test_netflix_monthly_four_times() -> None:
    res = detect(NETFLIX)
    assert len(res.suggestions) == 1
    s = res.suggestions[0]
    assert (s.name, s.bank_name, s.kind, s.frequency) == ("Netflix", "NETFLIX", "subscription", "monthly")
    assert (s.amount_cents, s.amount_varies, str(s.next_due)) == (1549, False, "2026-11-07")
    assert s.score == 90  # 60 + 10 (4th occurrence) + 20 × 1.0 regularity
    assert s.transaction_ids == [x["id"] for x in NETFLIX]


def test_rent_with_one_late_month() -> None:
    txs = [
        t("2026-06-01", -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT"),
        t("2026-07-01", -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT"),
        t("2026-08-04", -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT"),  # 34 days, rescued by day-of-month ±3
        t("2026-09-01", -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT"),
        t("2026-10-01", -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT"),
    ]
    [s] = detect(txs).suggestions
    assert (s.name, s.kind, s.frequency, s.amount_cents) == ("Oakwood Property", "bill", "monthly", 185000)
    assert str(s.next_due) == "2026-11-01"  # the usual day, not the late one
    assert s.score == 97  # 60 + 20 + 20 × 0.85


def test_biweekly_paycheck_is_income() -> None:
    start = date(2026, 7, 3)
    txs = [t(str(start + timedelta(days=14 * i)), 320000, "ACME CORP PAYROLL PPD ID: 123") for i in range(8)]
    [s] = detect(txs).suggestions
    assert (s.name, s.kind, s.frequency, s.amount_cents) == ("Acme Payroll", "income", "biweekly", 320000)
    assert s.next_due == start + timedelta(days=14 * 8)
    assert s.score == 100


def test_varying_utility_amount_varies() -> None:
    txs = [
        t("2026-07-20", -8000, "CITY OF AUSTIN UTIL WEB PMT"),
        t("2026-08-21", -9500, "CITY OF AUSTIN UTIL WEB PMT"),
        t("2026-09-19", -11000, "CITY OF AUSTIN UTIL WEB PMT"),
        t("2026-10-20", -8800, "CITY OF AUSTIN UTIL WEB PMT"),
    ]
    [s] = detect(txs).suggestions
    assert (s.name, s.amount_varies, s.amount_cents, s.kind) == ("City of Austin Util", True, 9150, "bill")


def test_fixed_amount_uses_latest_price() -> None:
    txs = monthly(7, [6, 7, 8], -1549, "NETFLIX.COM") + monthly(7, [9, 10], -1599, "NETFLIX.COM")
    [s] = detect(txs).suggestions
    assert (s.amount_cents, s.amount_varies) == (1599, False)


def test_wildly_different_amounts_are_not_a_bill() -> None:
    txs = [t("2026-07-03", -5000, "VENMO"), t("2026-08-03", -9000, "VENMO"), t("2026-09-03", -15000, "VENMO")]
    assert detect(txs).suggestions == []


def test_two_sample_cluster_rejected() -> None:
    assert detect(monthly(7, [9, 10], -1549, "NETFLIX.COM")).suggestions == []


def test_irregular_gaps_rejected() -> None:
    txs = [t("2026-07-01", -4500, "PLANET FITNESS"), t("2026-07-06", -4500, "PLANET FITNESS")]
    txs += [t("2026-07-26", -4500, "PLANET FITNESS"), t("2026-09-09", -4500, "PLANET FITNESS")]
    assert detect(txs).suggestions == []


def test_known_names_are_skipped() -> None:
    assert detect(NETFLIX, known=["Netflix"]).suggestions == []
    rent = monthly(1, [7, 8, 9, 10], -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT RENT")
    assert detect(rent, known=["Rent", "OAKWOOD PROPERTY"]).suggestions == []
    assert len(detect(rent, known=["Netflix"]).suggestions) == 1


def test_yearly_is_never_suggested() -> None:
    txs = [t(f"{y}-03-15", -9900, "AMAZON PRIME ANNUAL") for y in (2023, 2024, 2025, 2026)]
    assert detect(txs).suggestions == []


def test_weekly_and_quarterly_cadences() -> None:
    gym = [t(str(date(2026, 9, 1) + timedelta(days=7 * i)), -1500, "ORANGETHEORY FITNESS") for i in range(8)]
    water = [t(d, -6400, "SAWS WATER") for d in ("2026-01-15", "2026-04-15", "2026-07-15", "2026-10-15")]
    res = {s.bank_name: s for s in detect(gym + water).suggestions}
    assert res["ORANGETHEORY FITNESS"].frequency == "weekly"
    assert str(res["ORANGETHEORY FITNESS"].next_due) == "2026-10-27"
    assert res["SAWS WATER"].frequency == "quarterly"
    assert str(res["SAWS WATER"].next_due) == "2027-01-15"


def test_month_end_is_clamped() -> None:
    txs = [t(d, -2000, "GOOGLE ONE") for d in ("2026-07-31", "2026-08-31", "2026-09-30", "2026-10-31")]
    [s] = detect(txs, today="2026-11-02").suggestions
    assert (s.frequency, str(s.next_due), s.kind) == ("monthly", "2026-11-30", "subscription")


def test_stopped_payments_are_not_suggested() -> None:
    assert detect(monthly(7, [3, 4, 5, 6], -1549, "HULU")).suggestions == []


def test_subscription_found_among_one_off_purchases() -> None:
    txs = monthly(15, [7, 8, 9, 10], -1499, "AMAZON PRIME*2K4H71")
    txs += [t("2026-07-02", -3500, "AMZN Mktp US*1A2B3C"), t("2026-08-19", -12000, "AMZN Mktp US*9Z8Y7X")]
    txs += [t("2026-09-28", -800, "AMZN Mktp US*QQ11WW")]
    [s] = detect(txs).suggestions
    assert (s.name, s.kind, s.amount_cents, len(s.transaction_ids)) == (
        "Amazon Prime",
        "subscription",
        1499,
        4,
    )


def test_transfers_are_not_bills() -> None:
    assert detect(monthly(1, [7, 8, 9, 10], -50000, "ONLINE TRANSFER TO SAVINGS 1234")).suggestions == []


def test_duplicate_imports_are_collapsed() -> None:
    [s] = detect(NETFLIX + [dict(NETFLIX[-1], id="dup")]).suggestions
    assert len(s.transaction_ids) == 4


def test_inflows_and_outflows_cluster_separately() -> None:
    refunds = [t(f"2026-{m:02d}-09", 1549, "NETFLIX.COM", id=f"r{m}") for m in (8, 9, 10)]
    res = detect(NETFLIX + refunds).suggestions
    assert sorted(s.kind for s in res) == ["income", "subscription"]


def test_at_most_twenty_best_first() -> None:
    words = [
        *(
            "ALPHA",
            "BRAVO",
            "CHARLIE",
            "DELTA",
            "ECHO",
            "FOXTROT",
            "GOLF",
            "HOTEL",
            "INDIA",
            "JULIET",
            "KILO",
        ),
        *("LIMA", "MIKE", "OSCAR", "PAPA", "QUEBEC", "ROMEO", "SIERRA", "TANGO", "UNIFORM", "VICTOR"),
        *("WHISKEY", "XRAY", "YANKEE", "ZULU"),
    ]
    txs = []
    for i, w in enumerate(words):
        months = [5, 6, 7, 8, 9, 10] if i % 2 else [8, 9, 10]
        txs += monthly(10, months, -(1000 + i), f"{w} SERVICES")
    res = detect(txs).suggestions
    assert len(res) == 20
    scores = [s.score for s in res]
    assert scores == sorted(scores, reverse=True)
    assert scores[0] == 100 and scores[-1] == 80


@pytest.mark.parametrize(
    ("normalised", "display"),
    [
        ("NETFLIX", "Netflix"),
        ("OAKWOOD PROPERTY MGMT", "Oakwood Property"),
        ("CITY OF AUSTIN UTIL", "City of Austin Util"),
        ("AT&T", "AT&T"),
        ("HBO MAX", "HBO Max"),
        ("CVS", "CVS"),
        ("T MOBILE", "T Mobile"),
        ("ACME CORP PAYROLL", "Acme Payroll"),
    ],
)
def test_title_casing(normalised: str, display: str) -> None:
    assert title_case(normalised) == display


def test_kind_table() -> None:
    assert kind_for("SPOTIFY", inflow=False) == "subscription"
    assert kind_for("AMAZON PRIME", inflow=False) == "subscription"
    assert kind_for("AMAZON", inflow=False) == "bill"
    assert kind_for("NETFLIX", inflow=True) == "income"


def test_add_months_clamps() -> None:
    assert add_months(date(2026, 1, 31), 1) == date(2026, 2, 28)
    assert add_months(date(2026, 11, 15), 3) == date(2027, 2, 15)
    assert add_months(date(2026, 9, 30), 1, day=31) == date(2026, 10, 31)
