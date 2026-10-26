from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from matcher.app import Settings, create_app

TEST_KEY = "test-key-0123456789abcdef0123456789abcdef"


@pytest.fixture(scope="session")
def client() -> TestClient:
    with TestClient(create_app(Settings(key=TEST_KEY))) as c:
        yield c


@pytest.fixture
def auth() -> dict[str, str]:
    return {"X-Matcher-Key": TEST_KEY}
