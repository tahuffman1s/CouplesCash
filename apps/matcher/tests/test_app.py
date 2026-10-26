from __future__ import annotations

import importlib
import json
import logging
import tomllib
from datetime import date, timedelta
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from factories import cand, match_request, rule, tx
from matcher import __version__
from matcher.app import MAX_BODY_BYTES, ConfigError, Settings, create_app

SECRET_DESCRIPTION = "ACH DEBIT OAKWOOD PROPERTY MGMT RENT"


def rent_payload() -> dict:
    return match_request(
        [tx("2026-10-30", -185000, SECRET_DESCRIPTION, id="t1")],
        [cand("rent", "2026-11-01", 185000, ["Rent", "OAKWOOD PROPERTY"])],
        rules=[rule("KROGER", "groceries", 3)],
    )


def test_health_needs_no_key(client: TestClient) -> None:
    r = client.get("/health")
    assert r.status_code == 200
    assert r.json() == {"status": "ok", "version": __version__}


@pytest.mark.parametrize("path", ["/v1/match", "/v1/detect-recurring"])
@pytest.mark.parametrize("headers", [{}, {"X-Matcher-Key": "wrong"}, {"X-Matcher-Key": ""}])
def test_401_without_the_right_key(client: TestClient, path: str, headers: dict[str, str]) -> None:
    r = client.post(path, json={"today": "2026-10-26"}, headers=headers)
    assert r.status_code == 401
    assert r.headers["content-type"] == "application/problem+json"


def test_match_contract_shape(client: TestClient, auth: dict[str, str]) -> None:
    r = client.post("/v1/match", json=rent_payload(), headers=auth)
    assert r.status_code == 200, r.text
    body = r.json()
    assert set(body) == {"engine", "version", "matches", "categories"}
    assert body["engine"] == "fuzzy"
    assert body["matches"] == [
        {
            "transactionId": "t1",
            "itemId": "rent",
            "dueDate": "2026-11-01",
            "score": 97,
            "nameScore": 100,
            "amountScore": 100,
            "dateScore": 84,
            "auto": True,
            "reason": "name 100% · amount exact · 2 days early",
        }
    ]


def test_detect_recurring_contract_shape(client: TestClient, auth: dict[str, str]) -> None:
    txs = [
        {
            "id": f"n{m}",
            "postedOn": f"2026-{m:02d}-07",
            "amountCents": -1549,
            "description": "NETFLIX.COM 866-579-7172 CA",
        }
        for m in (7, 8, 9, 10)
    ]
    r = client.post(
        "/v1/detect-recurring",
        json={"today": "2026-10-26", "transactions": txs, "knownNames": []},
        headers=auth,
    )
    assert r.status_code == 200, r.text
    assert r.json() == {
        "suggestions": [
            {
                "name": "Netflix",
                "bankName": "NETFLIX",
                "kind": "subscription",
                "frequency": "monthly",
                "amountCents": 1549,
                "amountVaries": False,
                "nextDue": "2026-11-07",
                "score": 90,
                "transactionIds": ["n7", "n8", "n9", "n10"],
            }
        ]
    }


def test_413_too_many_transactions(client: TestClient, auth: dict[str, str]) -> None:
    txs = [{"id": str(i), "postedOn": "2026-10-01", "amountCents": -1} for i in range(5001)]
    for path in ("/v1/match", "/v1/detect-recurring"):
        r = client.post(path, json={"today": "2026-10-26", "transactions": txs}, headers=auth)
        assert r.status_code == 413, (path, r.text)
        assert "5001 transactions" in r.json()["detail"]


def test_413_too_many_candidates(client: TestClient, auth: dict[str, str]) -> None:
    cands = [
        {"itemId": str(i), "dueDate": "2026-10-01", "kind": "bill", "amountCents": 1, "names": []}
        for i in range(2001)
    ]
    r = client.post("/v1/match", json={"today": "2026-10-26", "candidates": cands}, headers=auth)
    assert r.status_code == 413
    assert "2001 candidates" in r.json()["detail"]


def test_5000_transactions_and_2000_candidates_are_fine(client: TestClient, auth: dict[str, str]) -> None:
    # A year of history against a year of occurrences (the limits, with realistic date spread).
    day = date(2025, 10, 26)
    txs = [
        {"id": str(i), "postedOn": str(day + timedelta(days=i % 365)), "amountCents": -(i + 1)}
        for i in range(5000)
    ]
    cands = [
        {
            "itemId": str(i),
            "dueDate": str(day + timedelta(days=i % 365)),
            "kind": "bill",
            "amountCents": i + 1,
        }
        for i in range(2000)
    ]
    body = json.dumps(
        {"today": "2026-10-26", "transactions": txs, "candidates": cands}, separators=(",", ":")
    )
    assert len(body) < MAX_BODY_BYTES
    r = client.post("/v1/match", content=body, headers={**auth, "Content-Type": "application/json"})
    assert r.status_code == 200


def test_413_body_over_one_megabyte(client: TestClient, auth: dict[str, str]) -> None:
    big = b'{"transactions": [], "pad": "' + b"x" * MAX_BODY_BYTES + b'"}'
    r = client.post("/v1/match", content=big, headers={**auth, "Content-Type": "application/json"})
    assert r.status_code == 413


def test_413_chunked_body_over_limit(client: TestClient, auth: dict[str, str]) -> None:
    def chunks():
        yield b'{"pad": "'
        for _ in range(9):
            yield b"x" * (128 * 1024)
        yield b'"}'

    r = client.post("/v1/match", content=chunks(), headers={**auth, "Content-Type": "application/json"})
    assert r.status_code == 413


def test_422_does_not_echo_input(client: TestClient, auth: dict[str, str]) -> None:
    payload = rent_payload()
    payload["transactions"][0]["postedOn"] = "not-a-date"
    r = client.post("/v1/match", json=payload, headers=auth)
    assert r.status_code == 422
    assert r.headers["content-type"] == "application/problem+json"
    assert "transactions.0.postedOn" in r.json()["errors"]
    assert SECRET_DESCRIPTION not in r.text


def test_logs_counts_never_descriptions(client: TestClient, auth: dict[str, str], caplog) -> None:
    logger = logging.getLogger("matcher")
    logger.addHandler(caplog.handler)
    try:
        with caplog.at_level(logging.INFO, logger="matcher"):
            client.post("/v1/match", json=rent_payload(), headers=auth)
    finally:
        logger.removeHandler(caplog.handler)
    text = caplog.text
    assert "match tx=1 candidates=1 rules=1 level=all → matches=1 auto=1 categories=0" in text
    assert "OAKWOOD" not in text and "Rent" not in text and "KROGER" not in text


def test_refuses_to_start_without_key() -> None:
    with pytest.raises(ConfigError, match="MATCHER_KEY is empty"):
        create_app(Settings(key=""))


def test_allow_no_key_for_tests() -> None:
    with TestClient(create_app(Settings(key="", allow_no_key=True))) as c:
        assert c.post("/v1/match", json=rent_payload()).status_code == 200


def test_module_app_reads_environment(monkeypatch: pytest.MonkeyPatch) -> None:
    import matcher.app as module

    monkeypatch.delenv("MATCHER_KEY", raising=False)
    monkeypatch.delenv("MATCHER_ALLOW_NO_KEY", raising=False)
    module = importlib.reload(module)
    with pytest.raises(SystemExit):
        _ = module.app
    monkeypatch.setenv("MATCHER_KEY", "k" * 64)
    module = importlib.reload(module)
    with TestClient(module.app) as c:
        assert (
            c.post("/v1/match", json=rent_payload(), headers={"X-Matcher-Key": "k" * 64}).status_code == 200
        )
        assert c.post("/v1/match", json=rent_payload()).status_code == 401


def test_no_interactive_docs(client: TestClient, auth: dict[str, str]) -> None:
    for path in ("/docs", "/redoc", "/openapi.json"):
        assert client.get(path, headers=auth).status_code == 404


def test_version_matches_pyproject() -> None:
    pyproject = tomllib.loads((Path(__file__).parents[1] / "pyproject.toml").read_text())
    assert pyproject["project"]["version"] == __version__
