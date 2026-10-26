"""FastAPI app: `GET /health`, `POST /v1/match`, `POST /v1/detect-recurring` (docs/MATCHER-CONTRACT.md).

Internal-only service on the compose network. Every request except `/health` must carry `X-Matcher-Key`
equal to `MATCHER_KEY` (constant-time compare). Stateless; logs carry request counts and timings, never
descriptions.

Environment:
  MATCHER_KEY            shared secret with the API (`Matcher__Key`); required: no key, no start
  MATCHER_ALLOW_NO_KEY   "1" runs without a key (tests / local experiments only)
  LOG_LEVEL              default INFO
"""

from __future__ import annotations

import logging
import os
import secrets
import time
from collections.abc import AsyncIterator, Awaitable, Callable, MutableMapping
from contextlib import asynccontextmanager
from dataclasses import dataclass
from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

from matcher.models import DetectRequest, DetectResponse, Health, MatchRequest, MatchResponse
from matcher.recurring import detect_recurring
from matcher.scoring import match
from matcher.version import __version__

MAX_BODY_BYTES = 1024 * 1024
MAX_TRANSACTIONS = 5000
MAX_CANDIDATES = 2000
KEY_HEADER = b"x-matcher-key"
PUBLIC_PATHS = frozenset({"/health"})

log = logging.getLogger("matcher")

Scope = MutableMapping[str, Any]
Message = MutableMapping[str, Any]
Receive = Callable[[], Awaitable[Message]]
Send = Callable[[Message], Awaitable[None]]
ASGIApp = Callable[[Scope, Receive, Send], Awaitable[None]]


class ConfigError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class Settings:
    key: str
    allow_no_key: bool = False

    @classmethod
    def from_env(cls) -> Settings:
        return cls(
            key=os.environ.get("MATCHER_KEY", "").strip(),
            allow_no_key=os.environ.get("MATCHER_ALLOW_NO_KEY", "") == "1",
        )

    def validate(self) -> None:
        if not self.key and not self.allow_no_key:
            raise ConfigError(
                "MATCHER_KEY is empty. Set it to the same value as the API's Matcher__Key "
                "(`make secrets` writes MATCHER_KEY to .env; compose passes it to both containers). "
                "Set MATCHER_ALLOW_NO_KEY=1 only for tests."
            )


def problem(status: int, title: str, detail: str | None = None, **extra: Any) -> JSONResponse:
    """RFC 9457 problem details, like the rest of the stack."""
    body: dict[str, Any] = {"type": "about:blank", "title": title, "status": status}
    if detail:
        body["detail"] = detail
    body.update(extra)
    return JSONResponse(body, status_code=status, media_type="application/problem+json")


class GuardMiddleware:
    """Pure ASGI: shared-key check before the body is read, then a buffered 1 MB body limit.

    Running before FastAPI means an unauthenticated or oversized request costs nothing to parse.
    """

    def __init__(self, app: ASGIApp, settings: Settings, max_body: int = MAX_BODY_BYTES) -> None:
        self.app = app
        self.expected = settings.key.encode()
        self.max_body = max_body

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http" or scope["path"] in PUBLIC_PATHS:
            await self.app(scope, receive, send)
            return
        headers = dict(scope.get("headers") or [])
        if self.expected:
            given = headers.get(KEY_HEADER, b"")
            if not secrets.compare_digest(given, self.expected):
                await problem(401, "Unauthorized", "missing or wrong X-Matcher-Key")(scope, receive, send)
                return
        declared = headers.get(b"content-length")
        if declared is not None and (not declared.isdigit() or int(declared) > self.max_body):
            await problem(413, "Payload too large", f"body limit is {self.max_body} bytes")(
                scope, receive, send
            )
            return
        chunks: list[bytes] = []
        size = 0
        more = True
        while more:
            message = await receive()
            if message["type"] == "http.disconnect":
                return
            chunk = message.get("body", b"")
            size += len(chunk)
            if size > self.max_body:
                await problem(413, "Payload too large", f"body limit is {self.max_body} bytes")(
                    scope, receive, send
                )
                return
            chunks.append(chunk)
            more = message.get("more_body", False)
        body = b"".join(chunks)
        replayed = False

        async def replay() -> Message:
            nonlocal replayed
            if not replayed:
                replayed = True
                return {"type": "http.request", "body": body, "more_body": False}
            return await receive()

        await self.app(scope, replay, send)


def _too_large(what: str, count: int, limit: int) -> JSONResponse:
    return problem(413, "Payload too large", f"{count} {what} (max {limit})")


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or Settings.from_env()

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        if not settings.key:
            log.warning("MATCHER_ALLOW_NO_KEY=1: requests are NOT authenticated (tests only)")
        elif len(settings.key) < 32:
            log.warning("MATCHER_KEY is shorter than 32 characters; generate one with `openssl rand -hex 32`")
        log.info("matcher %s ready", __version__)
        yield

    settings.validate()  # raises ConfigError: `uvicorn matcher.app:app` exits with the reason
    app = FastAPI(
        title="CouplesCash matcher",
        version=__version__,
        lifespan=lifespan,
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )

    @app.exception_handler(StarletteHTTPException)
    async def http_error(_: Request, exc: StarletteHTTPException) -> JSONResponse:
        return problem(exc.status_code, str(exc.detail))

    @app.exception_handler(RequestValidationError)
    async def validation_error(_: Request, exc: RequestValidationError) -> JSONResponse:
        # Field paths and messages only: never echo the input (it holds bank descriptions).
        errors: dict[str, list[str]] = {}
        for err in exc.errors():
            loc = ".".join(str(p) for p in err.get("loc", ()) if p != "body") or "body"
            errors.setdefault(loc, []).append(str(err.get("msg", "invalid")))
        log.info("rejected request: %d validation error(s)", len(errors))
        return problem(422, "Validation failed", errors=errors)

    @app.exception_handler(Exception)
    async def unexpected(_: Request, exc: Exception) -> JSONResponse:
        log.error("unhandled %s", type(exc).__name__)  # type only: messages may contain request data
        return problem(500, "Internal error")

    @app.get("/health", response_model=Health)
    async def health() -> Health:
        return Health(version=__version__)

    @app.post("/v1/match", response_model=MatchResponse, response_model_by_alias=True)
    def v1_match(req: MatchRequest) -> MatchResponse | JSONResponse:
        if len(req.transactions) > MAX_TRANSACTIONS:
            return _too_large("transactions", len(req.transactions), MAX_TRANSACTIONS)
        if len(req.candidates) > MAX_CANDIDATES:
            return _too_large("candidates", len(req.candidates), MAX_CANDIDATES)
        started = time.perf_counter()
        result = match(req)
        log.info(
            "match tx=%d candidates=%d rules=%d level=%s → matches=%d auto=%d categories=%d in %.1f ms",
            len(req.transactions),
            len(req.candidates),
            len(req.category_rules),
            req.options.auto_level,
            len(result.matches),
            sum(m.auto for m in result.matches),
            len(result.categories),
            (time.perf_counter() - started) * 1000,
        )
        return result

    @app.post("/v1/detect-recurring", response_model=DetectResponse, response_model_by_alias=True)
    def v1_detect(req: DetectRequest) -> DetectResponse | JSONResponse:
        if len(req.transactions) > MAX_TRANSACTIONS:
            return _too_large("transactions", len(req.transactions), MAX_TRANSACTIONS)
        started = time.perf_counter()
        result = detect_recurring(req)
        log.info(
            "detect-recurring tx=%d known=%d → suggestions=%d in %.1f ms",
            len(req.transactions),
            len(req.known_names),
            len(result.suggestions),
            (time.perf_counter() - started) * 1000,
        )
        return result

    # Added last = outermost user middleware: the key and the size are checked before FastAPI reads the body.
    app.add_middleware(GuardMiddleware, settings=settings)
    return app


def _configure_logging() -> None:
    level = os.environ.get("LOG_LEVEL", "INFO").upper()
    if not log.handlers:
        handler = logging.StreamHandler()
        handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s"))
        log.addHandler(handler)
    log.setLevel(level)
    log.propagate = False


_configure_logging()


def __getattr__(name: str) -> FastAPI:
    """`uvicorn matcher.app:app` builds the app on first access, so importing this module in tests is
    side-effect free, and a missing MATCHER_KEY exits with the explanation instead of starting."""
    if name == "app":
        try:
            application = create_app()
        except ConfigError as exc:
            log.critical("refusing to start: %s", exc)
            raise SystemExit(2) from None
        globals()["app"] = application
        return application
    raise AttributeError(name)
