"""`POST /v1/match`: score transactions against bill/income/one-off occurrences and learned category rules.

Rules (docs/MATCHER-CONTRACT.md "Scoring"):

* name score   max over the item's names of max(token_set_ratio, 0.95·partial_ratio, WRatio), normalised
* amount score sign must agree with the kind; exact → 100, inside tolerance → linear 100…70,
               outside → max(0, 70 − 4·excess %)
* date score   100 on the due date, linear to 60 at the window edge, pair skipped outside the window
* combined     0.45·name + 0.35·amount + 0.20·date
* auto         (combined ≥ autoThreshold ∧ name ≥ 70 ∧ amount inside tolerance)
               ∨ (amount exact ∧ |days| ≤ 3 ∧ name ≥ 50 ∧ only candidate with that exact amount in the window)
* assignment   greedy one-to-one by combined score over the whole batch; paid occurrences never assigned
* categories   for transactions left without a match: best rule by name score; auto at level `all` with
               name ≥ 90 ∧ hits ≥ 2, otherwise a suggestion at name ≥ 75

Component scores are rounded to integers first and the combined score is computed from those integers, so
every number in a response can be recomputed by hand (and by the API's built-in fallback) from those shown.
"""

from __future__ import annotations

import heapq
from bisect import bisect_left, bisect_right
from dataclasses import dataclass, field

from rapidfuzz import fuzz

from matcher.models import (
    CategoryMatch,
    CategoryRule,
    Match,
    MatchCandidate,
    MatchRequest,
    MatchResponse,
    MatchTransaction,
)
from matcher.normalize import normalize
from matcher.version import __version__

# --------------------------------------------------------------------------- tables

WEIGHT_NAME, WEIGHT_AMOUNT, WEIGHT_DATE = 0.45, 0.35, 0.20


@dataclass(frozen=True, slots=True)
class Tolerance:
    """Amount tolerance = max(pct % of the expected amount, min_cents); date window ±days."""

    pct: int
    min_cents: int
    days: int


TOL_RECURRING = Tolerance(pct=2, min_cents=100, days=5)
TOL_ONCE = Tolerance(pct=15, min_cents=1000, days=7)
TOL_VARIES = Tolerance(pct=35, min_cents=1500, days=10)
MAX_WINDOW_DAYS = max(t.days for t in (TOL_RECURRING, TOL_ONCE, TOL_VARIES))

INSIDE_FLOOR = 70  # amount score at the tolerance edge
OUTSIDE_SLOPE = 4  # points lost per % beyond the tolerance
DATE_EDGE = 60  # date score at the window edge

AUTO_MIN_NAME = 70
EXACT_AUTO_MAX_DAYS = 3
EXACT_AUTO_MIN_NAME = 50

MAX_PAIRS_PER_TRANSACTION = 8  # alternatives kept per transaction for the greedy assignment

CATEGORY_AUTO_NAME = 90
CATEGORY_AUTO_HITS = 2
CATEGORY_SUGGEST_NAME = 75

OUTFLOW_KINDS = frozenset({"bill", "subscription"})


def tolerance_for(frequency: str, amount_varies: bool) -> Tolerance:
    """The widest applicable rule wins: amountVaries ⊃ once ⊃ recurring."""
    if amount_varies:
        return TOL_VARIES
    if frequency.lower() == "once":
        return TOL_ONCE
    return TOL_RECURRING


# --------------------------------------------------------------------------- component scores


def _round(x: float) -> int:
    """Round half up (Python's round() is banker's rounding; C#'s fallback uses AwayFromZero)."""
    return int(x + 0.5) if x >= 0 else -int(-x + 0.5)


def name_score(a: str, b: str, cutoff: float = 0.0) -> float:
    """Contract name score of two already-normalised strings (0–100, unrounded).

    With `cutoff`, RapidFuzz may stop early and any score below the cutoff comes back as 0.
    """
    if not a or not b:
        return 0.0
    return max(
        fuzz.token_set_ratio(a, b, score_cutoff=cutoff),
        0.95 * fuzz.partial_ratio(a, b, score_cutoff=cutoff / 0.95),
        fuzz.WRatio(a, b, score_cutoff=cutoff),
    )


def best_name_score(texts: tuple[str, ...], names: tuple[str, ...], cutoff: float = 0.0) -> int:
    """Best rounded name score over every (transaction text, item name) pair."""
    best = 0.0
    for name in names:
        for text in texts:
            best = max(best, name_score(text, name, cutoff))
    return _round(best)


def tolerance_cents(expected: int, tol: Tolerance) -> int:
    return max(expected * tol.pct // 100, tol.min_cents)


def amount_score(
    actual: int, expected: int, tol: Tolerance, allowed: int | None = None
) -> tuple[int, int, int]:
    """(score, |difference| in cents, tolerance in cents) for absolute amounts."""
    diff = abs(actual - expected)
    if allowed is None:
        allowed = tolerance_cents(expected, tol)
    if diff == 0:
        return 100, 0, allowed
    if diff <= allowed:
        return _round(100 - (100 - INSIDE_FLOOR) * diff / allowed), diff, allowed
    excess_pct = (diff - allowed) * 100 / max(expected, 1)
    return max(0, _round(INSIDE_FLOOR - OUTSIDE_SLOPE * excess_pct)), diff, allowed


def date_score(days: int, tol: Tolerance) -> int | None:
    """Score for paying `days` after the due date (negative = early); None outside the window (skip it)."""
    distance = abs(days)
    if distance > tol.days:
        return None
    return _round(100 - (100 - DATE_EDGE) * distance / tol.days)


def combined_score(name: int, amount: int, date: int) -> int:
    return _round(WEIGHT_NAME * name + WEIGHT_AMOUNT * amount + WEIGHT_DATE * date)


def sign_agrees(amount_cents: int, kind: str) -> bool:
    if amount_cents < 0:
        return kind in OUTFLOW_KINDS
    if amount_cents > 0:
        return kind == "income"
    return False


# --------------------------------------------------------------------------- reasons


def money(cents: int) -> str:
    cents = abs(cents)
    return f"${cents // 100:,}.{cents % 100:02d}"


def _plural(n: int, word: str) -> str:
    return f"{n} {word}" if n == 1 else f"{n} {word}s"


def match_reason(name: int, diff: int, allowed: int, expected: int, days: int, exact_only: bool) -> str:
    parts = [f"name {name}%"]
    if diff == 0:
        parts.append("amount exact")
    elif diff <= allowed:
        parts.append(f"amount off by {money(diff)}")
    else:
        parts.append(f"amount off by {money(diff)} ({_round(diff * 100 / max(expected, 1))}%)")
    if days == 0:
        parts.append("on the due date")
    elif days < 0:
        parts.append(f"{_plural(-days, 'day')} early")
    else:
        parts.append(f"{_plural(days, 'day')} late")
    if exact_only:
        parts.append("only item with this amount")
    return " · ".join(parts)


# --------------------------------------------------------------------------- matching


@dataclass(slots=True)
class _Cand:
    index: int
    src: MatchCandidate
    due: int  # ordinal day
    expected: int
    names: tuple[str, ...]
    tol: Tolerance
    paid: bool
    key: tuple[str, int] = field(init=False)
    allowed: int = field(init=False)  # amount tolerance in cents

    def __post_init__(self) -> None:
        self.key = (self.src.item_id, self.due)
        self.allowed = tolerance_cents(self.expected, self.tol)


@dataclass(slots=True, frozen=True)
class _Pair:
    tx_index: int
    cand: _Cand
    combined: int
    name: int
    amount: int
    date: int
    days: int
    auto: bool
    by_exact_only: bool  # automatic only through the "only exact amount" rule
    diff: int
    allowed: int
    same_account: bool

    def sort_key(self) -> tuple[int, int, int, int, bool, int, int]:
        """Greedy order: combined, then amount, closeness, name, same account, input order."""
        return (
            -self.combined,
            -self.amount,
            abs(self.days),
            -self.name,
            not self.same_account,
            self.tx_index,
            self.cand.index,
        )

    def reason(self) -> str:
        return match_reason(
            self.name, self.diff, self.allowed, self.cand.expected, self.days, self.by_exact_only
        )


def transaction_texts(tx: MatchTransaction) -> tuple[str, ...]:
    """Normalised description and payee (both are scored; memo is not — it is mostly reference noise)."""
    texts = [normalize(tx.description)]
    if tx.payee:
        texts.append(normalize(tx.payee))
    return tuple(dict.fromkeys(t for t in texts if t))


def _prepare_candidates(candidates: list[MatchCandidate]) -> list[_Cand]:
    prepared = []
    for i, c in enumerate(candidates):
        names = tuple(dict.fromkeys(n for n in (normalize(x) for x in c.names) if n))
        prepared.append(
            _Cand(
                index=i,
                src=c,
                due=c.due_date.toordinal(),
                expected=abs(c.amount_cents),
                names=names,
                tol=tolerance_for(c.frequency, c.amount_varies),
                paid=c.paid,
            )
        )
    prepared.sort(key=lambda c: (c.due, c.index))
    return prepared


def _score_pairs(req: MatchRequest, cands: list[_Cand]) -> list[_Pair]:
    opts = req.options
    auto_enabled = opts.auto_level != "off"
    suggest = opts.suggest_threshold
    name_cache: dict[tuple[tuple[str, ...], tuple[str, ...]], int] = {}
    # Split by direction once (outflow ↔ bill/subscription, inflow ↔ income), each sorted by due date.
    by_sign = {sign: [c for c in cands if sign_agrees(sign, c.src.kind)] for sign in (-1, 1)}
    dues = {sign: [c.due for c in group] for sign, group in by_sign.items()}
    pairs: list[_Pair] = []
    for ti, tx in enumerate(req.transactions):
        if tx.amount_cents == 0:
            continue
        sign = -1 if tx.amount_cents < 0 else 1
        group, group_dues = by_sign[sign], dues[sign]
        posted = tx.posted_on.toordinal()
        actual = abs(tx.amount_cents)
        lo = bisect_left(group_dues, posted - MAX_WINDOW_DAYS)
        hi = bisect_right(group_dues, posted + MAX_WINDOW_DAYS)
        in_window = [(c, posted - c.due) for c in group[lo:hi] if -c.tol.days <= posted - c.due <= c.tol.days]
        if not in_window:
            continue
        # Paid occurrences still count here: an exact amount that also fits a paid bill is ambiguous.
        exact_in_window = sum(1 for c, _ in in_window if c.expected == actual)
        texts = transaction_texts(tx)
        tx_pairs: list[_Pair] = []
        for c, days in in_window:
            if c.paid:
                continue
            # Hot loop: the same arithmetic as date_score()/amount_score()/combined_score(), inlined
            # (tests/test_scoring.py::test_batch_equals_component_functions keeps them identical).
            window = c.tol.days
            d_score = int(100 - (100 - DATE_EDGE) * (days if days >= 0 else -days) / window + 0.5)
            allowed = c.allowed
            diff = actual - c.expected if actual >= c.expected else c.expected - actual
            if diff == 0:
                a_score = 100
            elif diff <= allowed:
                a_score = int(100 - (100 - INSIDE_FLOOR) * diff / allowed + 0.5)
            else:
                x = INSIDE_FLOOR - OUTSIDE_SLOPE * ((diff - allowed) * 100 / (c.expected or 1))
                a_score = int(x + 0.5) if x > 0 else 0
            if int(WEIGHT_NAME * 100 + WEIGHT_AMOUNT * a_score + WEIGHT_DATE * d_score + 0.5) < suggest:
                continue  # cannot reach the threshold even with a perfect name: skip the fuzzy work
            # Per-request cache: occurrences of an item share names, repeat merchants share texts.
            name_key = (texts, c.names)
            n_score = name_cache.get(name_key)
            if n_score is None:
                n_score = name_cache[name_key] = best_name_score(texts, c.names)
            combined = int(WEIGHT_NAME * n_score + WEIGHT_AMOUNT * a_score + WEIGHT_DATE * d_score + 0.5)
            if combined < suggest:
                continue
            by_threshold = combined >= opts.auto_threshold and n_score >= AUTO_MIN_NAME and diff <= allowed
            by_exact = (
                diff == 0
                and abs(days) <= EXACT_AUTO_MAX_DAYS
                and n_score >= EXACT_AUTO_MIN_NAME
                and exact_in_window == 1
            )
            auto = auto_enabled and (by_threshold or by_exact)
            tx_pairs.append(
                _Pair(
                    tx_index=ti,
                    cand=c,
                    combined=combined,
                    name=n_score,
                    amount=a_score,
                    date=d_score,
                    days=days,
                    auto=auto,
                    by_exact_only=auto and not by_threshold,
                    diff=diff,
                    allowed=allowed,
                    same_account=bool(tx.account_id and tx.account_id == c.src.account_id),
                )
            )
        # Bound memory on pathological batches: a transaction keeps only its best alternatives.
        if len(tx_pairs) > MAX_PAIRS_PER_TRANSACTION:
            tx_pairs = heapq.nsmallest(MAX_PAIRS_PER_TRANSACTION, tx_pairs, key=_Pair.sort_key)
        pairs.extend(tx_pairs)
    return pairs


def _assign(pairs: list[_Pair]) -> dict[int, _Pair]:
    """Greedy one-to-one assignment, best combined score first across the whole batch."""
    pairs.sort(key=_Pair.sort_key)
    taken_tx: set[int] = set()
    taken_cand: set[tuple[str, int]] = set()
    chosen: dict[int, _Pair] = {}
    for p in pairs:
        if p.tx_index in taken_tx or p.cand.key in taken_cand:
            continue
        taken_tx.add(p.tx_index)
        taken_cand.add(p.cand.key)
        chosen[p.tx_index] = p
    return chosen


def _best_rule(
    texts: tuple[str, ...], rules: list[tuple[tuple[str, ...], CategoryRule]]
) -> tuple[int, CategoryRule] | None:
    best: tuple[int, int, CategoryRule] | None = None
    for norm, r in rules:
        score = best_name_score(texts, norm, CATEGORY_SUGGEST_NAME - 0.5)  # lower scores are dropped anyway
        if best is None or (score, r.hits) > (best[0], best[1]):
            best = (score, r.hits, r)
    return None if best is None else (best[0], best[2])


def _categorise(req: MatchRequest, matched: set[int]) -> list[CategoryMatch]:
    rules = [((n,), r) for n, r in ((normalize(r.payee), r) for r in req.category_rules) if n]
    if not rules:
        return []
    allow_auto = req.options.auto_level == "all"
    by_text: dict[tuple[str, ...], tuple[int, CategoryRule] | None] = {}  # repeat merchants share the work
    out: list[CategoryMatch] = []
    for ti, tx in enumerate(req.transactions):
        # Category rules describe expenses: only money going out is categorised.
        if ti in matched or tx.amount_cents >= 0:
            continue
        texts = transaction_texts(tx)
        if texts not in by_text:
            by_text[texts] = _best_rule(texts, rules)
        best = by_text[texts]
        if best is None:
            continue
        score, rule = best
        auto = allow_auto and score >= CATEGORY_AUTO_NAME and rule.hits >= CATEGORY_AUTO_HITS
        if not auto and score < CATEGORY_SUGGEST_NAME:
            continue
        out.append(
            CategoryMatch(
                transaction_id=tx.id,
                category_id=rule.category_id,
                score=score,
                auto=auto,
                reason=f"payee {rule.payee.strip()} ({_plural(rule.hits, 'confirmation')})",
            )
        )
    return out


def match(req: MatchRequest) -> MatchResponse:
    cands = _prepare_candidates(req.candidates)
    chosen = _assign(_score_pairs(req, cands))
    matches = [
        Match(
            transaction_id=req.transactions[ti].id,
            item_id=p.cand.src.item_id,
            due_date=p.cand.src.due_date,
            score=p.combined,
            name_score=p.name,
            amount_score=p.amount,
            date_score=p.date,
            auto=p.auto,
            reason=p.reason(),
        )
        for ti, p in sorted(chosen.items())
    ]
    categories = _categorise(req, set(chosen))
    return MatchResponse(version=__version__, matches=matches, categories=categories)
