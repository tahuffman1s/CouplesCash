# syntax=docker/dockerfile:1
# CouplesCash matcher — FastAPI + RapidFuzz, multi-stage, multi-arch (linux/amd64 + linux/arm64).
# Build context is apps/matcher:   docker build -t couplescash-matcher apps/matcher
#
# Both stages use the same python:3.13-slim image so the venv's interpreter symlinks stay valid. The builder runs
# on the *target* platform (RapidFuzz and pydantic-core ship native wheels per architecture); under QEMU that only
# means unpacking wheels, nothing is compiled. (Literal FROM lines so Dependabot can bump them together.)

FROM ghcr.io/astral-sh/uv:0.11.32 AS uv

FROM python:3.13-slim AS build
COPY --from=uv /uv /usr/local/bin/uv
ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_PYTHON_DOWNLOADS=never \
    UV_PROJECT_ENVIRONMENT=/opt/venv
WORKDIR /app
# Dependencies only (no dev group, no project install): this layer is cached until uv.lock changes.
COPY pyproject.toml uv.lock ./
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --frozen --no-dev --no-install-project
COPY src/ ./src/
# Read-only root FS at runtime + PYTHONDONTWRITEBYTECODE: ship the bytecode instead of compiling on every start.
RUN python -m compileall -q src

FROM python:3.13-slim AS runtime
ENV PATH=/opt/venv/bin:$PATH \
    PYTHONPATH=/app/src \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    MALLOC_ARENA_MAX=2
RUN groupadd --system --gid 10001 matcher \
 && useradd --system --uid 10001 --gid matcher --no-create-home --shell /usr/sbin/nologin matcher
WORKDIR /app
COPY --from=build /opt/venv /opt/venv
COPY --from=build /app/src /app/src
USER matcher:matcher
EXPOSE 8090

# The slim image has no curl; Python's urllib is enough. compose.yml repeats this probe.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["python", "-c", "import sys, urllib.request; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8090/health', timeout=4).status == 200 else 1)"]

# One worker: the service is CPU-light and stateless, and a second interpreter would double the RAM on a 2 GB Pi.
CMD ["uvicorn", "matcher.app:app", "--host", "0.0.0.0", "--port", "8090", "--workers", "1", "--no-access-log"]
