# CouplesCash — developer & Raspberry Pi task runner.
# Run `make` (or `make help`) to list targets. Lines ending in `## text` are the docs.

SHELL        := /usr/bin/env bash
.SHELLFLAGS  := -eu -o pipefail -c
.DEFAULT_GOAL := help
MAKEFLAGS    += --no-builtin-rules --no-print-directory

COMPOSE      := docker compose
COMPOSE_DEV  := docker compose -f compose.yml -f compose.dev.yml
DOTNET       ?= dotnet
PNPM         ?= pnpm
UV           ?= uv
UV_RUN       := $(UV) run --frozen
API_DIR      := apps/api
API_HOST_DIR := apps/api/CouplesCash.Api
API_SLN      := apps/api/CouplesCash.slnx
WEB_DIR      := apps/web
MATCHER_DIR  := apps/matcher
DB_SERVICE   := db
DB_USER      := couplescash
DB_NAME      := couplescash
EVERY        ?= 4
BACKUP_DIR   := backups
STAMP        := $(shell date +%Y%m%d-%H%M%S)
# Guard against an api build that does not know `--vapid` yet and would start Kestrel instead of exiting.
TIMEOUT      := $(shell command -v timeout >/dev/null 2>&1 && echo 'timeout 600' || true)

.PHONY: help setup secrets matcher-key vapid dev-env dev-db dev-api dev-web dev-matcher dev migrate up down logs \
        scheduler-logs matcher-logs ps build pull tunnel-up tunnel-down pi backup backup-nextcloud \
        backup-nextcloud-cron restore test test-api test-web \
        test-matcher lint fmt clean

##@ Getting started

help: ## Show this help
	@awk 'BEGIN {FS = ":.*##"; printf "\nCouplesCash\n\nUsage: make \033[36m<target>\033[0m [VAR=value]\n"} \
	  /^##@/ { printf "\n\033[1m%s\033[0m\n", substr($$0, 5) } \
	  /^[a-zA-Z0-9_-]+:.*?##/ { printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2 }' $(MAKEFILE_LIST)
	@echo

setup: ## Check tools, create .env with fresh secrets, install deps (one-time)
	@for t in docker $(DOTNET) node $(PNPM) $(UV) openssl; do \
	  command -v $$t >/dev/null 2>&1 && printf '  ok   %s\n' "$$t" || { printf '  MISSING %s\n' "$$t"; missing=1; }; \
	done; \
	docker compose version >/dev/null 2>&1 && echo '  ok   docker compose' || { echo '  MISSING docker compose plugin'; missing=1; }; \
	[ -z "$${missing:-}" ] || { echo; echo 'Install the missing tools and re-run `make setup`.'; \
	  echo '(uv: curl -LsSf https://astral.sh/uv/install.sh | sh — it also provides Python 3.13 for the matcher)'; exit 1; }
	@$(MAKE) .env secrets dev-env
	@echo '→ dotnet restore'; $(DOTNET) restore $(API_SLN)
	@echo '→ pnpm install';   cd $(WEB_DIR) && $(PNPM) install
	@echo '→ uv sync';        cd $(MATCHER_DIR) && $(UV) sync --frozen
	@echo; echo 'Done. Next: `make dev` (db in Docker, api + web natively) — web on http://localhost:5173'

.env:
	@[ -f .env ] || { cp .env.example .env && chmod 600 .env && echo "Created .env from .env.example"; }

secrets: .env ## Fill empty JWT_SECRET / POSTGRES_PASSWORD / DATA_KEY / MATCHER_KEY in .env (FORCE=1 regenerates all — breaks existing bank links)
	@fill() { \
	  key=$$1; val=$$2; \
	  if [ -n "$${FORCE:-}" ] || grep -qE "^$$key=\s*$$" .env; then \
	    if grep -qE "^$$key=" .env; then sed -i "s|^$$key=.*|$$key=$$val|" .env; else printf '%s=%s\n' "$$key" "$$val" >> .env; fi; \
	    echo "  set  $$key"; \
	  else echo "  keep $$key (already set; use FORCE=1 to rotate)"; fi; \
	}; \
	if [ -n "$${FORCE:-}" ] && grep -qE "^DATA_KEY=.+" .env; then \
	  echo; echo '  !!! WARNING: FORCE=1 rotates DATA_KEY. Every linked bank becomes unreadable and must be'; \
	  echo '  !!! re-linked in Settings → Linked banks (docs/BANK-SYNC.md). Back up .env first.'; echo; \
	fi; \
	fill JWT_SECRET        "$$(openssl rand -base64 48 | tr -d '\n')"; \
	fill POSTGRES_PASSWORD "$$(openssl rand -hex 24)"; \
	fill DATA_KEY          "$$(openssl rand -base64 32 | tr -d '\n')"; \
	fill MATCHER_KEY       "$$(openssl rand -hex 32)"
	@$(MAKE) vapid
	@$(MAKE) dev-env

matcher-key: .env ## Fill an empty MATCHER_KEY (api ↔ matcher shared key) in .env; `make up` runs this
	@if grep -qE '^MATCHER_KEY=.+' .env; then :; \
	elif grep -qE '^#?\s*MATCHER_KEY=' .env; then sed -i "s|^#\?\s*MATCHER_KEY=.*|MATCHER_KEY=$$(openssl rand -hex 32)|" .env; echo '  set  MATCHER_KEY'; \
	else printf 'MATCHER_KEY=%s\n' "$$(openssl rand -hex 32)" >> .env; echo '  set  MATCHER_KEY'; fi

vapid: .env ## Fill empty VAPID_PUBLIC_KEY / VAPID_PRIVATE_KEY (Web Push) in .env; FORCE=1 rotates (every phone must re-enable notifications)
	@set_kv() { \
	  if grep -qE "^#?\s*$$1=" .env; then sed -i "s|^#\?\s*$$1=.*|$$1=$$2|" .env; else printf '%s=%s\n' "$$1" "$$2" >> .env; fi; \
	}; \
	get_kv() { grep -E "^$$1=" .env | head -n1 | cut -d= -f2- || true; }; \
	if [ -z "$${FORCE:-}" ] && [ -n "$$(get_kv VAPID_PUBLIC_KEY)" ] && [ -n "$$(get_kv VAPID_PRIVATE_KEY)" ]; then \
	  echo '  keep VAPID_PUBLIC_KEY / VAPID_PRIVATE_KEY (already set; use FORCE=1 to rotate)'; \
	elif command -v $(DOTNET) >/dev/null 2>&1 && [ -f $(API_HOST_DIR)/CouplesCash.Api.csproj ]; then \
	  echo '  generating VAPID keys with: dotnet run -- --vapid'; \
	  out="$$(cd $(API_HOST_DIR) && $(TIMEOUT) $(DOTNET) run --no-launch-profile -- --vapid 2>&1)" || { printf '%s\n' "$$out" | tail -n 20; echo '  FAILED: dotnet run -- --vapid'; exit 1; }; \
	  pub="$$(printf '%s\n' "$$out" | sed -n 's/^Push__VapidPublicKey=//p' | tail -n1)"; \
	  priv="$$(printf '%s\n' "$$out" | sed -n 's/^Push__VapidPrivateKey=//p' | tail -n1)"; \
	  [ -n "$$pub" ] && [ -n "$$priv" ] || { printf '%s\n' "$$out" | tail -n 20; echo '  FAILED: could not parse --vapid output'; exit 1; }; \
	  set_kv VAPID_PUBLIC_KEY "$$pub"; set_kv VAPID_PRIVATE_KEY "$$priv"; echo '  set  VAPID_PUBLIC_KEY / VAPID_PRIVATE_KEY'; \
	elif docker info >/dev/null 2>&1; then \
	  echo '  generating VAPID keys with: docker compose run api dotnet CouplesCash.Api.dll --vapid'; \
	  out="$$($(TIMEOUT) $(COMPOSE) run --rm --no-deps -T api dotnet CouplesCash.Api.dll --vapid 2>&1)" || { printf '%s\n' "$$out" | tail -n 20; echo '  FAILED: docker compose run api --vapid (is the api image pulled/built? try `make pull`)'; exit 1; }; \
	  pub="$$(printf '%s\n' "$$out" | sed -n 's/^Push__VapidPublicKey=//p' | tail -n1)"; \
	  priv="$$(printf '%s\n' "$$out" | sed -n 's/^Push__VapidPrivateKey=//p' | tail -n1)"; \
	  [ -n "$$pub" ] && [ -n "$$priv" ] || { printf '%s\n' "$$out" | tail -n 20; echo '  FAILED: could not parse --vapid output'; exit 1; }; \
	  set_kv VAPID_PUBLIC_KEY "$$pub"; set_kv VAPID_PRIVATE_KEY "$$priv"; echo '  set  VAPID_PUBLIC_KEY / VAPID_PRIVATE_KEY'; \
	else \
	  echo '  skip VAPID keys: need the dotnet SDK or a running docker daemon. Push stays disabled (503) until you run `make vapid`.'; \
	fi; \
	subj="$$(get_kv VAPID_SUBJECT)"; \
	if [ -z "$$subj" ]; then \
	  host="$$(get_kv PUBLIC_URL | sed -E 's#^[a-z]+://##; s#[:/].*$$##')"; \
	  set_kv VAPID_SUBJECT "mailto:admin@$${host:-localhost}"; echo "  set  VAPID_SUBJECT=mailto:admin@$${host:-localhost} (edit .env to change)"; \
	fi

dev-env: .env ## Write .env.dev (derived env for running api/web natively & VS Code debugging)
	@scripts/dev-env.sh

##@ Development (db in Docker, api + web + matcher natively with hot reload)

dev: ## Start db, then api (dotnet watch) + web (vite) + matcher (uvicorn) together; Ctrl-C stops them
	@scripts/dev.sh

dev-db: .env ## Start only Postgres, published on 127.0.0.1:5432
	$(COMPOSE_DEV) up -d --wait $(DB_SERVICE)

dev-api: dev-env ## Run the API natively with hot reload on http://localhost:8080
	@set -a; source .env.dev; set +a; cd $(API_HOST_DIR) && $(DOTNET) watch run --no-launch-profile

dev-web: dev-env ## Run the SvelteKit dev server on http://localhost:5173
	@set -a; source .env.dev; set +a; cd $(WEB_DIR) && $(PNPM) dev --host

dev-matcher: dev-env ## Run the fuzzy matcher natively with hot reload on http://localhost:8090
	@set -a; source .env.dev; set +a; cd $(MATCHER_DIR) && \
	  $(UV_RUN) uvicorn matcher.app:app --reload --reload-dir src --host 127.0.0.1 --port 8090

migrate: .env ## Run database migrations (DbUp) using the api image
	$(COMPOSE) run --rm migrate

##@ Stack (Docker Compose, production-like)

up: .env matcher-key ## Build/pull as needed and start db + migrate + matcher + api + web
	$(COMPOSE) up -d --remove-orphans
	@$(MAKE) ps

down: ## Stop and remove containers (data volume is kept)
	$(COMPOSE) --profile tunnel down --remove-orphans

logs: ## Tail logs of all services (S=api to filter)
	$(COMPOSE) --profile tunnel logs -f --tail=100 $(S)

scheduler-logs: ## Tail api logs from the background scheduler only (autopay, reminders, hygiene)
	$(COMPOSE) logs -f --tail=1000 api | grep --line-buffered -i 'scheduler'

matcher-logs: ## Tail the fuzzy matcher's logs (one line per batch: counts and timings, never descriptions)
	$(COMPOSE) logs -f --tail=200 matcher

ps: ## Show service status and health
	$(COMPOSE) --profile tunnel ps

build: ## Build api + web + matcher images locally (slow on a Pi; prefer `make pull`)
	$(COMPOSE) build --pull api web matcher

pull: ## Pull the latest published images from GHCR
	$(COMPOSE) --profile tunnel pull

tunnel-up: .env matcher-key ## Start everything including the Cloudflare tunnel
	$(COMPOSE) --profile tunnel up -d --remove-orphans
	@$(MAKE) ps

tunnel-down: ## Stop only the tunnel container
	$(COMPOSE) --profile tunnel stop cloudflared

pi: ## Raspberry Pi deploy/update: pull images, (re)start stack with tunnel
	@$(MAKE) pull
	@$(MAKE) tunnel-up
	@docker image prune -f >/dev/null && echo 'Pruned old images.'

##@ Data

backup: ## pg_dump → backups/couplescash-<timestamp>.sql.gz + a copy of .env (holds DATA_KEY; KEEP=14 rotation)
	@scripts/backup.sh

backup-nextcloud: ## pg_dump + a copy of .env → your Nextcloud folder over WebDAV (NEXTCLOUD_* in .env; keeps NEXTCLOUD_KEEP=42 pairs)
	@scripts/backup-nextcloud.sh

backup-nextcloud-cron: ## Run backup-nextcloud every EVERY=4 hours from your crontab (re-run to change; EVERY=0 removes it)
	@[[ "$(EVERY)" =~ ^([0-9]|1[0-9]|2[0-4])$$ ]] || { echo 'EVERY must be 0-24 (hours)'; exit 2; }
	@mkdir -p $(BACKUP_DIR) && chmod 700 $(BACKUP_DIR)
	@{ crontab -l 2>/dev/null | grep -v 'make backup-nextcloud' || true; \
	  [ "$(EVERY)" = 0 ] || echo '17 */$(EVERY) * * * cd $(CURDIR) && make backup-nextcloud >> $(BACKUP_DIR)/nextcloud.log 2>&1'; \
	} | crontab -
	@if [ "$(EVERY)" = 0 ]; then echo 'Removed the Nextcloud backup from your crontab.'; \
	else echo 'Nextcloud backup runs every $(EVERY) h (at :17). Log: $(BACKUP_DIR)/nextcloud.log'; fi

restore: ## Restore a dump: make restore FILE=backups/couplescash-….sql.gz (restore the matching .env copy too, or bank links are unreadable)
	@[ -n "$(FILE)" ] || { echo 'Usage: make restore FILE=backups/couplescash-YYYYmmdd-HHMMSS.sql.gz'; exit 2; }
	@[ -f "$(FILE)" ] || { echo "No such file: $(FILE)"; exit 2; }
	@echo "About to restore $(FILE) into the live database (api + web will be stopped)."; \
	read -r -p 'Type "yes" to continue: ' ans; [ "$$ans" = yes ] || { echo aborted; exit 1; }
	$(COMPOSE) stop api web
	gunzip -c "$(FILE)" | $(COMPOSE) exec -T $(DB_SERVICE) psql -v ON_ERROR_STOP=1 -q -U $(DB_USER) -d $(DB_NAME)
	$(COMPOSE) start api web
	@echo 'Restore complete.'
	@env_copy="$${FILE%.sql.gz}.env"; \
	if [ -f "$$env_copy" ] && ! cmp -s "$$env_copy" .env; then \
	  echo "Note: $$env_copy differs from the current .env. Linked banks were encrypted with the DATA_KEY in that"; \
	  echo "      backup — copy it over .env (and \`make up\`) if Settings → Linked banks shows decryption errors."; \
	fi

##@ Quality

test: test-api test-web test-matcher ## Run all tests (dotnet test + Playwright e2e + pytest)

test-api: ## dotnet test
	$(DOTNET) test $(API_SLN)

test-web: ## Playwright e2e (installs chromium on first run)
	cd $(WEB_DIR) && $(PNPM) exec playwright install chromium && $(PNPM) exec playwright test

test-matcher: ## pytest for the fuzzy matcher
	cd $(MATCHER_DIR) && $(UV_RUN) pytest

lint: ## dotnet format --verify-no-changes + pnpm check + pnpm lint + ruff check/format --check
	$(DOTNET) format $(API_SLN) --verify-no-changes
	cd $(WEB_DIR) && $(PNPM) check && $(PNPM) lint
	cd $(MATCHER_DIR) && $(UV_RUN) ruff check . && $(UV_RUN) ruff format --check .

fmt: ## Auto-format C# (dotnet format), web (prettier) and matcher (ruff)
	$(DOTNET) format $(API_SLN)
	cd $(WEB_DIR) && $(PNPM) format
	cd $(MATCHER_DIR) && $(UV_RUN) ruff check --fix . && $(UV_RUN) ruff format .

clean: ## Remove build artefacts (bin/obj, .svelte-kit, build, reports, Python caches). Keeps node_modules, .venv, data
	find $(API_DIR) -type d \( -name bin -o -name obj -o -name TestResults \) -prune -exec rm -rf {} +
	rm -rf $(WEB_DIR)/.svelte-kit $(WEB_DIR)/build $(WEB_DIR)/playwright-report $(WEB_DIR)/test-results
	find $(MATCHER_DIR) -type d -name __pycache__ -not -path '*/.venv/*' -prune -exec rm -rf {} +
	rm -rf $(MATCHER_DIR)/.pytest_cache $(MATCHER_DIR)/.ruff_cache
