"""Small builders for request payloads (camelCase, exactly what the API sends)."""

from __future__ import annotations

from itertools import count
from typing import Any

_ids = count(1)


def tx(posted_on: str, amount_cents: int, description: str, **extra: Any) -> dict[str, Any]:
    return {
        "id": extra.pop("id", f"tx-{next(_ids)}"),
        "accountId": extra.pop("accountId", None),
        "postedOn": posted_on,
        "amountCents": amount_cents,
        "description": description,
        "payee": extra.pop("payee", None),
        "memo": None,
        "pending": extra.pop("pending", False),
        **extra,
    }


def cand(
    item_id: str,
    due_date: str,
    amount_cents: int,
    names: list[str],
    kind: str = "bill",
    frequency: str = "monthly",
    **extra: Any,
) -> dict[str, Any]:
    return {
        "itemId": item_id,
        "dueDate": due_date,
        "kind": kind,
        "frequency": frequency,
        "amountCents": amount_cents,
        "amountVaries": extra.pop("amountVaries", False),
        "names": names,
        "accountId": extra.pop("accountId", None),
        "paid": extra.pop("paid", False),
        **extra,
    }


def match_request(
    transactions: list[dict[str, Any]],
    candidates: list[dict[str, Any]] | None = None,
    rules: list[dict[str, Any]] | None = None,
    auto_level: str = "all",
    auto_threshold: int = 85,
    suggest_threshold: int = 55,
) -> dict[str, Any]:
    return {
        "today": "2026-10-26",
        "options": {
            "autoLevel": auto_level,
            "autoThreshold": auto_threshold,
            "suggestThreshold": suggest_threshold,
        },
        "transactions": transactions,
        "candidates": candidates or [],
        "categoryRules": rules or [],
    }


def rule(payee: str, category_id: str, hits: int) -> dict[str, Any]:
    return {"payee": payee, "categoryId": category_id, "hits": hits}
