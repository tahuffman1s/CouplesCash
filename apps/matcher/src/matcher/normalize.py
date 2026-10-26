"""Normalisation of bank descriptions, payees and item names (docs/MATCHER-CONTRACT.md "Normalise").

Everything is table-driven: add a row to one of the tables below rather than a new code path. The same
function is applied to both sides of a comparison (transaction text and the item's names / rule payees), so
dropping a word is symmetric and cannot by itself create a mismatch.

Pipeline (order matters):
  1. upper-case, strip accents
  2. merchant aliases (``AMZN Mktp US*2K4H71`` → ``AMAZON``) — before anything that would break their patterns
  3. payment-processor prefixes (``SQ *``, ``TST*``, ``PAYPAL *`` …)
  4. noise patterns: ``ID:…``, card fragments, phone numbers, dates/times, store numbers, ``.COM``
  5. punctuation → spaces
  6. noise words (``POS``, ``DEBIT``, ``ACH`` …), long reference numbers
  7. trailing country / state code / known city
  8. drop repeated tokens, collapse whitespace; if nothing is left, fall back to the plain upper-cased text
"""

from __future__ import annotations

import re
import unicodedata
from functools import lru_cache

# --------------------------------------------------------------------------- tables

# 2. Merchant aliases: (pattern, replacement). Applied in order, on the upper-cased text.
ALIASES: tuple[tuple[str, str], ...] = (
    (r"\bAMZN\s*MKTP\s*[A-Z]{2}(?:\s*\*\s*[A-Z0-9]+)?", " AMAZON "),
    (r"\bAMAZON\s*(?:MKTPL?|MKTPLACE|MARKETPLACE)\b(?:\s*\*\s*[A-Z0-9]+)?", " AMAZON "),
    (r"\bAMAZON\.COM\s*\*\s*[A-Z0-9]+", " AMAZON "),
    (r"\bAMZN\.COM/BILL\b", " AMAZON "),
    (r"\bAMZN\b", " AMAZON "),
    (r"\bAPPLE\.COM/BILL\b", " APPLE "),
    (r"\bWAL-?MART\b|\bWM\s+SUPERCENTER\b|\bWAL\s*SAMS\b", " WALMART "),
    (r"\bWHOLEFDS\b", " WHOLE FOODS "),
    (r"\bCOSTCO\s+WHSE\b", " COSTCO "),
    (r"\bVZWRLSS\b|\bVERIZON\s+WRLS\b", " VERIZON WIRELESS "),
    (r"\bT-MOBILE\b|\bTMOBILE\b", " T MOBILE "),
    (
        r"\bSPOTIFY\s*(?:USA?I?|AB)\b",
        " SPOTIFY ",
    ),  # PayPal descriptor "SPOTIFYUSAI", "SPOTIFY USA", "SPOTIFY AB"
    (r"\bNETFLIX\s*(?:INC|COM)\b", " NETFLIX "),
)

# 3. Payment processors / aggregators that prefix the real merchant ("SQ *BLUE BOTTLE", "TST* JOES").
PROCESSOR_PREFIXES: tuple[str, ...] = (
    "SQ",  # Square
    "SQU",
    "TST",  # Toast
    "PAYPAL",
    "PP",  # PayPal (short form)
    "SP",  # Shopify
    "PY",  # Paymentus / Payway
    "IN",  # Invoiced / Intuit
    "BT",  # Braintree
    "FS",  # FastSpring
    "CKO",  # Checkout.com
    "DNH",  # GoDaddy
    "EB",  # Eventbrite
    "WPY",  # WePay
    "ZTL",  # Zettle
    "LS",  # Lightspeed
)

# 4. Noise patterns removed outright (replaced with a space).
NOISE_PATTERNS: tuple[str, ...] = (
    r"\b(?:PPD|CCD|WEB|TEL|CO|IND)?\s*ID\s*[:#]\s*[A-Z0-9-]+",  # PPD ID: 123, ID:9876, CO ID:XYZ
    r"\b(?:ORIG\s+CO\s+NAME|CO\s+ENTRY\s+DESCR|ENTRY\s+DESCR|SEC|TRACE\s*#?|INDN|DES|CONF\s*#?)\s*:",  # ACH
    r"\b(?:X{2,}|\*{2,})\s*\d{2,}\b",  # XXXX1234, ****1234
    r"\*\s*\d{2,}\b",  # *1234
    r"\bCARD\s*(?:ENDING\s*(?:IN\s*)?)?#?\s*\d{4}\b",  # CARD 1234, CARD ENDING IN 1234
    r"\(?\b\d{3}\)?[-. ]?\d{3}[-. ]\d{4}\b",  # 866-579-7172, (800) 555.1212
    r"\b1?-?8\d{2}-[A-Z]{4,7}\b",  # vanity toll-free numbers: 800-COMCAST
    r"\b1?\d{10}\b",  # 8665797172
    r"\b\d{4}-\d{2}-\d{2}\b",  # 2026-10-24
    r"\b\d{1,2}/\d{1,2}(?:/\d{2,4})?\b",  # 10/24, 10/24/26
    r"\b(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEPT?|OCT|NOV|DEC)\s?\d{1,2}(?:,?\s?\d{4})?\b",  # OCT 24
    r"\b\d{1,2}:\d{2}(?::\d{2})?\b",  # 14:32
    r"#\s*\d+",  # #123
    r"\b(?:STORE|STR|STE|NO)\.?\s*#?\s*\d+\b",  # STORE 0456, STE 200
    r"\bWWW\.",
    r"\bG\.CO/\S*",  # Google's "g.co/helppay#" billing descriptor suffix
    r"\.(?:COM|NET|ORG|IO)\b",  # NETFLIX.COM → NETFLIX
)

# 6. Words that carry no merchant information.
NOISE_WORDS: frozenset[str] = frozenset(
    {
        # from the contract
        "POS",
        "DEBIT",
        "CREDIT",
        "PURCHASE",
        "ACH",
        "CHECKCARD",
        "RECURRING",
        "PAYMENT",
        "ONLINE",
        "WEB",
        "PPD",
        # common bank boilerplate with the same meaning
        "PMT",
        "PYMT",
        "AUTOPAY",
        "PREAUTHORIZED",
        "DBT",
        "CCD",
        "CARD",
        "TRANSACTION",
        "ORIG",
        "DESCR",
        "ENTRY",
        "INDN",
    }
)

# 7. Trailing location tokens.
COUNTRY_CODES: frozenset[str] = frozenset({"US", "USA"})
STATE_CODES: frozenset[str] = frozenset(
    [
        "AL",
        "AK",
        "AZ",
        "AR",
        "CA",
        "CO",
        "CT",
        "DE",
        "DC",
        "FL",
        "GA",
        "HI",
        "ID",
        "IL",
        "IN",
        "IA",
        "KS",
        "KY",
        "LA",
        "ME",
        "MD",
        "MA",
        "MI",
        "MN",
        "MS",
        "MO",
        "MT",
        "NE",
        "NV",
        "NH",
        "NJ",
        "NM",
        "NY",
        "NC",
        "ND",
        "OH",
        "OK",
        "OR",
        "PA",
        "PR",
        "RI",
        "SC",
        "SD",
        "TN",
        "TX",
        "UT",
        "VT",
        "VA",
        "WA",
        "WV",
        "WI",
        "WY",
    ]
)
# Cities that commonly trail a merchant name. Multi-word cities are matched as token sequences.
CITIES: tuple[tuple[str, ...], ...] = tuple(
    tuple(c.split())
    for c in (
        "NEW YORK",
        "BROOKLYN",
        "LOS ANGELES",
        "SAN FRANCISCO",
        "SAN JOSE",
        "SAN DIEGO",
        "OAKLAND",
        "BERKELEY",
        "SACRAMENTO",
        "SEATTLE",
        "PORTLAND",
        "AUSTIN",
        "DALLAS",
        "HOUSTON",
        "SAN ANTONIO",
        "FORT WORTH",
        "CHICAGO",
        "DENVER",
        "BOULDER",
        "PHOENIX",
        "LAS VEGAS",
        "SALT LAKE CITY",
        "ATLANTA",
        "MIAMI",
        "ORLANDO",
        "TAMPA",
        "BOSTON",
        "CAMBRIDGE",
        "PHILADELPHIA",
        "PITTSBURGH",
        "WASHINGTON",
        "BALTIMORE",
        "DETROIT",
        "MINNEAPOLIS",
        "SAINT PAUL",
        "ST PAUL",
        "ST LOUIS",
        "KANSAS CITY",
        "NASHVILLE",
        "CHARLOTTE",
        "RALEIGH",
        "COLUMBUS",
        "CLEVELAND",
        "CINCINNATI",
        "INDIANAPOLIS",
        "MILWAUKEE",
        "NEW ORLEANS",
        "LOS GATOS",
        "MOUNTAIN VIEW",
        "PALO ALTO",
        "SUNNYVALE",
        "CUPERTINO",
        "REDMOND",
        "BELLEVUE",
    )
)
# A trailing city is only dropped on its own (without a state code after it) when it is not part of a name
# like "CITY OF AUSTIN" or "BANK OF BOSTON".
CITY_GUARD_WORDS: frozenset[str] = frozenset({"OF", "CITY", "BANK"})

# --------------------------------------------------------------------------- compiled

_ALIASES = tuple((re.compile(p), r) for p, r in ALIASES)
_PROCESSOR = re.compile(r"\b(?:" + "|".join(PROCESSOR_PREFIXES) + r")\s?\*\s*")
_NOISE = tuple(re.compile(p) for p in NOISE_PATTERNS)
_APOSTROPHE = re.compile(r"['`’]")
_NON_WORD = re.compile(r"[^A-Z0-9&]+")
_LONG_NUMBER = re.compile(r"^\d{4,}$")
_DIGIT = re.compile(r"\d")
_WS = re.compile(r"\s+")


def _strip_accents(text: str) -> str:
    decomposed = unicodedata.normalize("NFKD", text)
    return "".join(ch for ch in decomposed if not unicodedata.combining(ch))


def _is_reference(token: str) -> bool:
    """Long numbers and mixed letter/digit ids (``2K4H71Q``) identify a transaction, not a merchant."""
    if _LONG_NUMBER.match(token):
        return True
    return len(token) >= 6 and len(_DIGIT.findall(token)) >= 3 and not token.isdigit()


def _ends_with(tokens: list[str], seq: tuple[str, ...]) -> bool:
    return len(tokens) > len(seq) and tuple(tokens[-len(seq) :]) == seq


def _strip_location(tokens: list[str]) -> list[str]:
    # "… NY USA", "… USA NY": peel country and state codes in either order.
    had_state = False
    while len(tokens) > 1:
        if tokens[-1] in COUNTRY_CODES:
            tokens = tokens[:-1]
        elif tokens[-1] in STATE_CODES and not had_state:
            tokens = tokens[:-1]
            had_state = True
        else:
            break
    for city in CITIES:
        if _ends_with(tokens, city):
            before = tokens[: -len(city)]
            if had_state or (len(before) >= 2 and before[-1] not in CITY_GUARD_WORDS):
                tokens = before
            break
    return tokens


def _fallback(upper: str) -> str:
    return _WS.sub(" ", _NON_WORD.sub(" ", _APOSTROPHE.sub("", upper))).strip()


@lru_cache(maxsize=8192)
def normalize(text: str | None) -> str:
    """Normalise a bank description, payee or item name for fuzzy comparison. Pure and cached."""
    if not text:
        return ""
    upper = _strip_accents(text).upper()
    s = upper
    for pattern, replacement in _ALIASES:
        s = pattern.sub(replacement, s)
    s = _PROCESSOR.sub(" ", s)
    for pattern in _NOISE:
        s = pattern.sub(" ", s)
    s = _APOSTROPHE.sub("", s)
    s = _NON_WORD.sub(" ", s)
    tokens = [t for t in s.split() if t not in NOISE_WORDS and not _is_reference(t)]
    tokens = _strip_location(list(dict.fromkeys(tokens)))
    result = " ".join(tokens)
    return result or _fallback(upper)
