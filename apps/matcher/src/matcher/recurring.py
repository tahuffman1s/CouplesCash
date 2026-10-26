"""`POST /v1/detect-recurring`: find regular payments ("found bills") in a household's imported transactions.

Rules (docs/MATCHER-CONTRACT.md):

1. Cluster outflows and inflows separately by normalised name (token_set_ratio ≥ 85).
2. A cluster needs ≥ 3 transactions whose consecutive gaps all fit one frequency: weekly 7±1, biweekly 14±2,
   monthly 28–33 days or the same day-of-month ±3 one calendar month later, quarterly 85–95. Yearly is never
   suggested (two samples are not enough evidence).
3. Amounts within 10 % of the median → fixed amount (the latest one); within 35 % → `amountVaries` (the
   median); otherwise not a bill. When the whole cluster fails, amount bands inside it are tried, so a monthly
   subscription is still found among one-off purchases from the same merchant.
4. nextDue = last date + period (monthly/quarterly keep the usual day-of-month, clamped to the month's end).
5. kind: inflow → income; known streaming/software names → subscription; else bill.
6. score = 60 + 10 per occurrence beyond 3 (max +20) + 20 × regularity, capped at 100.
7. Skip clusters that fuzzy-match a known item name/bank name ≥ 85 and clusters that stopped (no payment for
   more than two periods). At most 20 suggestions, best first.
"""

from __future__ import annotations

import calendar
from collections import Counter
from dataclasses import dataclass
from datetime import date, timedelta

from rapidfuzz import fuzz, process

from matcher.models import DetectRequest, DetectResponse, RecurringSuggestion
from matcher.normalize import normalize
from matcher.scoring import _round, name_score

CLUSTER_RATIO = 85
KNOWN_NAME_SCORE = 85
MIN_OCCURRENCES = 3
FIXED_AMOUNT_PCT = 10
VARIES_AMOUNT_PCT = 35
BAND_SPREAD = 1.35  # an amount band holds amounts up to 35 % above its smallest member
MAX_SUGGESTIONS = 20
BASE_SCORE = 60
PER_EXTRA = 10
MAX_EXTRA = 20
REGULARITY_WEIGHT = 20


@dataclass(frozen=True, slots=True)
class Cadence:
    name: str  # contract frequency
    min_gap: int
    max_gap: int
    ideal: float  # for regularity of day-count cadences
    months: int  # 0 = day-count cadence; > 0 = calendar months (keeps the day-of-month)
    stale_after_days: int  # no payment for this long → the payment stopped


WEEKLY = Cadence("weekly", 6, 8, 7, 0, 14)
BIWEEKLY = Cadence("biweekly", 12, 16, 14, 0, 28)
MONTHLY = Cadence("monthly", 28, 33, 30.44, 1, 62)
QUARTERLY = Cadence("quarterly", 85, 95, 91.31, 3, 184)
CADENCES = (WEEKLY, BIWEEKLY, MONTHLY, QUARTERLY)
DAY_OF_MONTH_SLACK = 3

# Normalised names that are subscriptions rather than bills (whole tokens or token sequences).
SUBSCRIPTION_NAMES: tuple[str, ...] = (
    "NETFLIX",
    "SPOTIFY",
    "HULU",
    "DISNEY",
    "DISNEYPLUS",
    "HBO",
    "MAX",
    "HBOMAX",
    "YOUTUBE",
    "YOUTUBEPREMIUM",
    "APPLE",
    "ICLOUD",
    "ITUNES",
    "GOOGLE STORAGE",
    "GOOGLE ONE",
    "GOOGLE PLAY",
    "AMAZON PRIME",
    "PRIME VIDEO",
    "AUDIBLE",
    "KINDLE",
    "PARAMOUNT",
    "PEACOCK",
    "STARZ",
    "SHOWTIME",
    "CRUNCHYROLL",
    "ESPN",
    "SLING",
    "FUBO",
    "PHILO",
    "DISCOVERY",
    "PANDORA",
    "TIDAL",
    "DEEZER",
    "SIRIUSXM",
    "SIRIUS XM",
    "ADOBE",
    "MICROSOFT",
    "MSFT",
    "XBOX",
    "PLAYSTATION",
    "NINTENDO",
    "STEAM",
    "TWITCH",
    "DROPBOX",
    "GITHUB",
    "OPENAI",
    "CHATGPT",
    "ANTHROPIC",
    "CLAUDE",
    "NOTION",
    "EVERNOTE",
    "1PASSWORD",
    "LASTPASS",
    "BITWARDEN",
    "NORDVPN",
    "EXPRESSVPN",
    "CANVA",
    "GRAMMARLY",
    "ZOOM",
    "SLACK",
    "PATREON",
    "SUBSTACK",
    "NYTIMES",
    "NEW YORK TIMES",
    "WSJ",
    "WASHINGTON POST",
    "DUOLINGO",
    "CALM",
    "HEADSPACE",
    "PELOTON",
    "STRAVA",
    "MIDJOURNEY",
)
# Business-form words dropped from the display name ("OAKWOOD PROPERTY MGMT" → "Oakwood Property").
CORE_DROP_WORDS: frozenset[str] = frozenset(
    [
        "MGMT",
        "MGT",
        "INC",
        "INCORPORATED",
        "LLC",
        "LLP",
        "LP",
        "PLLC",
        "CORP",
        "CORPORATION",
        "CO",
        "LTD",
        "SVC",
        "SVCS",
    ]
)
LOWER_WORDS: frozenset[str] = frozenset(["OF", "AND", "THE", "FOR", "AT", "IN", "ON", "TO", "A", "AN", "BY"])
ACRONYMS: frozenset[str] = frozenset(
    [
        "HBO",
        "USAA",
        "IRS",
        "DMV",
        "HOA",
        "YMCA",
        "YWCA",
        "ATM",
        "USPS",
        "UPS",
        "ESPN",
        "AAA",
        "AARP",
        "ADT",
        "IKEA",
        "BMW",
        "KFC",
        "TJ",
        "ATT",
        "GE",
        "PGE",
        "PG&E",
        "SDGE",
        "SCE",
        "TXU",
        "DTE",
        "NYC",
        "LA",
        "TV",
        "HSA",
        "FSA",
        "IRA",
    ]
)
# Never "bills": money moved between the household's own accounts.
EXCLUDE_WORDS: frozenset[str] = frozenset({"TRANSFER", "XFER"})
_VOWELS = frozenset("AEIOUY")
_SUBSCRIPTION_SEQS = tuple(tuple(n.split()) for n in SUBSCRIPTION_NAMES)


@dataclass(frozen=True, slots=True)
class _Tx:
    id: str
    on: date
    amount: int  # absolute cents
    inflow: bool
    name: str  # normalised


# --------------------------------------------------------------------------- helpers


def add_months(d: date, months: int, day: int | None = None) -> date:
    """`d` + `months` calendar months on `day` (default d.day), clamped to the target month's length."""
    total = d.year * 12 + (d.month - 1) + months
    year, month = divmod(total, 12)
    month += 1
    last = calendar.monthrange(year, month)[1]
    return date(year, month, min(day or d.day, last))


def _month_index(d: date) -> int:
    return d.year * 12 + d.month


def _dom_distance(a: date, b: date) -> int:
    """Day-of-month distance, treating every month-end as the same day (Jan 31 ~ Feb 28)."""
    da = min(a.day, 28) if a.day >= calendar.monthrange(a.year, a.month)[1] else a.day
    db = min(b.day, 28) if b.day >= calendar.monthrange(b.year, b.month)[1] else b.day
    return abs(da - db)


def _median_x2(values: list[int]) -> int:
    """Twice the median (an integer even for an even count)."""
    s = sorted(values)
    n = len(s)
    return 2 * s[n // 2] if n % 2 else s[n // 2 - 1] + s[n // 2]


def title_case(name: str) -> str:
    """Display name: core of a normalised name, title-cased ("OAKWOOD PROPERTY MGMT" → "Oakwood Property")."""
    tokens = [t for t in name.split() if t not in CORE_DROP_WORDS] or name.split()
    words = []
    for i, t in enumerate(tokens):
        if t in ACRONYMS or "&" in t or len(t) == 1:
            words.append(t)
        elif i > 0 and t in LOWER_WORDS:
            words.append(t.lower())
        elif t.isalpha() and len(t) <= 4 and not (set(t) & _VOWELS):
            words.append(t)  # CVS, BP, DMV
        else:
            words.append(t[0] + t[1:].lower())
    return " ".join(words)


def kind_for(name: str, inflow: bool) -> str:
    if inflow:
        return "income"
    tokens = name.split()
    for seq in _SUBSCRIPTION_SEQS:
        n = len(seq)
        if any(tuple(tokens[i : i + n]) == seq for i in range(len(tokens) - n + 1)):
            return "subscription"
    return "bill"


# --------------------------------------------------------------------------- clustering


def _cluster(txs: list[_Tx]) -> list[list[_Tx]]:
    """Group identical names, then merge groups whose names agree (token_set_ratio ≥ 85), biggest first."""
    groups: dict[str, list[_Tx]] = {}
    for t in txs:
        groups.setdefault(t.name, []).append(t)
    reps: list[str] = []  # each cluster's representative (its biggest group's name)
    clusters: list[list[_Tx]] = []
    for name, members in sorted(groups.items(), key=lambda kv: (-len(kv[1]), kv[0])):
        hit = process.extractOne(name, reps, scorer=fuzz.token_set_ratio, score_cutoff=CLUSTER_RATIO)
        if hit is not None:
            clusters[hit[2]].extend(members)
        else:
            reps.append(name)
            clusters.append(list(members))
    return clusters


def _fit_cadence(dates: list[date]) -> tuple[Cadence, float] | None:
    """The cadence every consecutive gap fits, with regularity 0–1 (1 = perfectly even)."""
    gaps = [(b - a).days for a, b in zip(dates, dates[1:], strict=False)]
    for cad in CADENCES:
        if cad.months:
            ok = all(
                cad.min_gap <= g <= cad.max_gap
                or (
                    _month_index(b) - _month_index(a) == cad.months
                    and _dom_distance(a, b) <= DAY_OF_MONTH_SLACK
                )
                for g, a, b in zip(gaps, dates, dates[1:], strict=False)
            )
            if not ok:
                continue
            usual = _usual_day(dates)
            deviations = [abs(d.day - min(usual, calendar.monthrange(d.year, d.month)[1])) for d in dates]
            slack = DAY_OF_MONTH_SLACK + 1
        else:
            if not all(cad.min_gap <= g <= cad.max_gap for g in gaps):
                continue
            deviations = [abs(g - cad.ideal) for g in gaps]
            slack = (cad.max_gap - cad.min_gap) / 2 + 1
        regularity = 1 - sum(min(1.0, dev / slack) for dev in deviations) / len(deviations)
        return cad, max(0.0, regularity)
    return None


def _usual_day(dates: list[date]) -> int:
    """Most common day-of-month (ties → the most recent one's)."""
    counts = Counter(d.day for d in dates)
    top = max(counts.values())
    return next(d.day for d in reversed(dates) if counts[d.day] == top)


def _next_due(dates: list[date], cad: Cadence) -> date:
    last = dates[-1]
    if cad.months:
        return add_months(last, cad.months, _usual_day(dates))
    return last + timedelta(days=int(cad.ideal))


def _evaluate(members: list[_Tx], today: date) -> RecurringSuggestion | None:
    seen: set[tuple[date, int]] = set()
    txs = []
    for t in sorted(members, key=lambda t: (t.on, t.id)):
        if (t.on, t.amount) in seen:  # the same payment imported twice (e.g. CSV + bank sync)
            continue
        seen.add((t.on, t.amount))
        txs.append(t)
    if len(txs) < MIN_OCCURRENCES:
        return None
    dates = [t.on for t in txs]
    if len(set(dates)) != len(dates):
        return None  # two different charges on one day is not a single regular payment
    fit = _fit_cadence(dates)
    if fit is None:
        return None
    cad, regularity = fit
    if (today - dates[-1]).days > cad.stale_after_days:
        return None

    amounts = [t.amount for t in txs]
    m2 = _median_x2(amounts)
    if m2 <= 0:
        return None
    spread = max(abs(2 * a - m2) for a in amounts) * 100  # % of the median × m2
    if spread <= FIXED_AMOUNT_PCT * m2:
        varies, amount = False, amounts[-1]
    elif spread <= VARIES_AMOUNT_PCT * m2:
        varies, amount = True, _round(m2 / 2)
    else:
        return None

    names = Counter(t.name for t in txs)
    bank_name = max(names.items(), key=lambda kv: (kv[1], -len(kv[0])))[0]
    inflow = txs[0].inflow
    extra = min(MAX_EXTRA, PER_EXTRA * (len(txs) - MIN_OCCURRENCES))
    score = min(100, _round(BASE_SCORE + extra + REGULARITY_WEIGHT * regularity))
    return RecurringSuggestion(
        name=title_case(bank_name),
        bank_name=bank_name,
        kind=kind_for(bank_name, inflow),
        frequency=cad.name,
        amount_cents=amount,
        amount_varies=varies,
        next_due=_next_due(dates, cad),
        score=score,
        transaction_ids=[t.id for t in txs],
    )


def _bands(members: list[_Tx]) -> list[list[_Tx]]:
    out: list[list[_Tx]] = []
    for t in sorted(members, key=lambda t: t.amount):
        if out and t.amount <= out[-1][0].amount * BAND_SPREAD:
            out[-1].append(t)
        else:
            out.append([t])
    return [b for b in out if len(b) >= MIN_OCCURRENCES]


def _best_for_cluster(members: list[_Tx], today: date) -> RecurringSuggestion | None:
    whole = _evaluate(members, today)
    if whole is not None:
        return whole
    found = [s for band in _bands(members) if len(band) < len(members) and (s := _evaluate(band, today))]
    return max(found, key=lambda s: (s.score, len(s.transaction_ids)), default=None)


# --------------------------------------------------------------------------- entry point


def _is_known(name: str, known: list[str]) -> bool:
    return any(name_score(name, k) >= KNOWN_NAME_SCORE for k in known)


def detect_recurring(req: DetectRequest) -> DetectResponse:
    txs: list[_Tx] = []
    for t in req.transactions:
        if t.amount_cents == 0:
            continue
        name = normalize(t.payee) if t.payee and normalize(t.payee) else normalize(t.description)
        if not name or EXCLUDE_WORDS.intersection(name.split()):
            continue
        txs.append(_Tx(t.id, t.posted_on, abs(t.amount_cents), t.amount_cents > 0, name))
    known = list(dict.fromkeys(k for k in (normalize(n) for n in req.known_names) if k))

    suggestions: list[RecurringSuggestion] = []
    for inflow in (False, True):
        for members in _cluster([t for t in txs if t.inflow is inflow]):
            if len(members) < MIN_OCCURRENCES:
                continue
            if any(_is_known(n, known) for n in {m.name for m in members}):
                continue
            s = _best_for_cluster(members, req.today)
            if s is not None:
                suggestions.append(s)
    suggestions.sort(key=lambda s: (-s.score, -len(s.transaction_ids), s.name, s.kind))
    return DetectResponse(suggestions=suggestions[:MAX_SUGGESTIONS])
