from __future__ import annotations

import pytest

from matcher.normalize import normalize


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("ACH DEBIT OAKWOOD PROPERTY MGMT RENT", "OAKWOOD PROPERTY MGMT RENT"),
        ("NETFLIX.COM 866-579-7172 CA", "NETFLIX"),
        ("SQ *BLUE BOTTLE COFFEE OAKLAND CA", "BLUE BOTTLE COFFEE"),
        ("AMZN Mktp US*2K4H71", "AMAZON"),
        ("PAYPAL *SPOTIFYUSAI", "SPOTIFY"),
        ("STATE FARM INSURANCE 800-956-6310", "STATE FARM INSURANCE"),
        ("CITY OF AUSTIN UTIL WEB PMT", "CITY OF AUSTIN UTIL"),
        ("JIFFY LUBE #1234", "JIFFY LUBE"),
        ("ACME CORP PAYROLL PPD ID: 123", "ACME CORP PAYROLL"),
        ("POS PURCHASE KROGER #0456 XXXX1234 10/24", "KROGER"),
        ("TST* JOES PIZZA", "JOES PIZZA"),
        ("WAL-MART #1234 AUSTIN TX", "WALMART"),
        ("SPOTIFY USA 877-778-1161 NY", "SPOTIFY"),
        ("COMCAST CABLE COMM 800-COMCAST", "COMCAST CABLE COMM"),
        ("CHECKCARD 1024 SHELL OIL 57444 STORE 0456", "SHELL OIL"),
        ("Café Olé", "CAFE OLE"),
    ],
)
def test_realistic_bank_strings(raw: str, expected: str) -> None:
    assert normalize(raw) == expected


def test_item_names_normalise_the_same_way() -> None:
    assert normalize("Rent") == "RENT"
    assert normalize("Oakwood Property") == "OAKWOOD PROPERTY"
    assert normalize("netflix") == normalize("NETFLIX.COM")


def test_city_inside_a_name_is_kept() -> None:
    # "CITY OF AUSTIN" is the merchant, not a trailing location.
    assert normalize("CITY OF AUSTIN") == "CITY OF AUSTIN"


def test_empty_and_noise_only_inputs() -> None:
    assert normalize(None) == ""
    assert normalize("") == ""
    # Nothing but noise words → fall back to the plain upper-cased text instead of an empty string.
    assert normalize("ONLINE PAYMENT") == "ONLINE PAYMENT"
