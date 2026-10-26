"""Request/response models for docs/MATCHER-CONTRACT.md (camelCase on the wire, snake_case in Python)."""

from __future__ import annotations

from datetime import date
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

Id = Annotated[str, Field(min_length=1, max_length=64)]
Text = Annotated[str, Field(max_length=1024)]
Score = Annotated[int, Field(ge=0, le=100)]

AutoLevel = Literal["off", "bills", "all"]
Kind = Literal["bill", "subscription", "income"]


class CamelModel(BaseModel):
    """camelCase aliases; unknown fields are ignored, so the API can add fields without breaking this."""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")


# --------------------------------------------------------------------------- POST /v1/match


class MatchOptions(CamelModel):
    auto_level: AutoLevel = "all"
    auto_threshold: Score = 85
    suggest_threshold: Score = 55


class MatchTransaction(CamelModel):
    id: Id
    account_id: Id | None = None
    posted_on: date
    amount_cents: int  # negative = money out, positive = money in
    description: Text = ""
    payee: Text | None = None
    memo: Text | None = None
    pending: bool = False


class MatchCandidate(CamelModel):
    item_id: Id
    due_date: date
    kind: Kind
    frequency: Annotated[str, Field(max_length=32)] = "monthly"
    amount_cents: int  # expected amount, positive
    amount_varies: bool = False
    names: Annotated[list[Text], Field(max_length=64)] = []
    account_id: Id | None = None
    paid: bool = False


class CategoryRule(CamelModel):
    payee: Text
    category_id: Id
    hits: Annotated[int, Field(ge=0)] = 0


class MatchRequest(CamelModel):
    today: date | None = None
    options: MatchOptions = MatchOptions()
    transactions: list[MatchTransaction] = []
    candidates: list[MatchCandidate] = []
    category_rules: list[CategoryRule] = []


class Match(CamelModel):
    transaction_id: str
    item_id: str
    due_date: date
    score: Score
    name_score: Score
    amount_score: Score
    date_score: Score
    auto: bool
    reason: str


class CategoryMatch(CamelModel):
    transaction_id: str
    category_id: str
    score: Score
    auto: bool
    reason: str


class MatchResponse(CamelModel):
    engine: Literal["fuzzy"] = "fuzzy"
    version: str
    matches: list[Match]
    categories: list[CategoryMatch]


# --------------------------------------------------------------------------- POST /v1/detect-recurring


class DetectTransaction(CamelModel):
    id: Id
    posted_on: date
    amount_cents: int
    description: Text = ""
    payee: Text | None = None


class DetectRequest(CamelModel):
    today: date
    transactions: list[DetectTransaction] = []
    known_names: Annotated[list[Text], Field(max_length=5000)] = []


Frequency = Literal["weekly", "biweekly", "monthly", "quarterly"]


class RecurringSuggestion(CamelModel):
    name: str
    bank_name: str
    kind: Kind
    frequency: Frequency
    amount_cents: int
    amount_varies: bool
    next_due: date
    score: Score
    transaction_ids: list[str]


class DetectResponse(CamelModel):
    suggestions: list[RecurringSuggestion]


# --------------------------------------------------------------------------- GET /health


class Health(BaseModel):
    status: Literal["ok"] = "ok"
    version: str
