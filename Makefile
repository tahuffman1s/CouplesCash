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
API_DIR      := apps/api
API_HOST_DIR := apps/api/CouplesCash.Api
API_SLN      := apps/api/CouplesCash.slnx
WEB_DIR      := apps/web
DB_SERVICE   := db
DB_USER      := couplescash
DB_NAME      := couplescash
BACKUP_DIR   := backups
STAMP        := $(shell date +%Y%m%d-%H%M%S)

.PHONY: help setup secrets dev-env dev-db dev-api dev-web dev migrate up down logs ps build pull \
        tunnel-up tunnel-down pi backup restore test test-api test-web lint fmt clean

##@ Getting started

help: ## Show this help
	@awk 'BEGIN {FS = ":.*##"; printf "\nCouplesCash\n\nUsage: make \033[36m<target>\033[0m [VAR=value]\n"} \
	  /^##@/ { printf "\n\033[1m%s\033[0m\n", substr($$0, 5) } \
	  /^[a-zA-Z0-9_-]+:.*?##/ { printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2 }' $(MAKEFILE_LIST)
	@echo

setup: ## Check tools, create .env with fresh secrets, install deps (one-time)
	@for t in docker $(DOTNET) node $(PNPM) openssl; do \
	  command -v $$t >/dev/null 2>&1 && printf '  ok   %s\n' "$$t" || { printf '  MISSING %s\n' "$$t"; missing=1; }; \
	done; \
	docker compose version >/dev/null 2>&1 && echo '  ok   docker compose' || { echo '  MISSING docker compose plugin'; missing=1; }; \
	[ -z "$${missing:-}" ] || { echo; echo 'Install the missing tools and re-run `make setup`.'; exit 1; }
	@$(MAKE) .env secrets dev-env
	@echo '→ dotnet restore'; $(DOTNET) restore $(API_SLN)
	@echo '→ pnpm install';   cd $(WEB_DIR) && $(PNPM) install
	@echo; echo 'Done. Next: `make dev` (db in Docker, api + web natively) — web on http://localhost:5173'

.env:
	@[ -f .env ] || { cp .env.example .env && chmod 600 .env && echo "Created .env from .env.example"; }

secrets: .env ## Fill empty JWT_SECRET / POSTGRES_PASSWORD in .env (FORCE=1 to regenerate)
	@fill() { \
	  key=$$1; val=$$2; \
	  if [ -n "$${FORCE:-}" ] || grep -qE "^$$key=\s*$$" .env; then \
	    if grep -qE "^$$key=" .env; then sed -i "s|^$$key=.*|$$key=$$val|" .env; else printf '%s=%s\n' "$$key" "$$val" >> .env; fi; \
	    echo "  set  $$key"; \
	  else echo "  keep $$key (already set; use FORCE=1 to rotate)"; fi; \
	}; \
	fill JWT_SECRET        "$$(openssl rand -base64 48 | tr -d '\n')"; \
	fill POSTGRES_PASSWORD "$$(openssl rand -hex 24)"
	@$(MAKE) dev-env

dev-env: .env ## Write .env.dev (derived env for running api/web natively & VS Code debugging)
	@scripts/dev-env.sh

##@ Development (db in Docker, api + web natively with hot reload)

dev: ## Start db, then api (dotnet watch) + web (vite) together; Ctrl-C stops both
	@scripts/dev.sh

dev-db: .env ## Start only Postgres, published on 127.0.0.1:5432
	$(COMPOSE_DEV) up -d --wait $(DB_SERVICE)

dev-api: dev-env ## Run the API natively with hot reload on http://localhost:8080
	@set -a; source .env.dev; set +a; cd $(API_HOST_DIR) && $(DOTNET) watch run --no-launch-profile

dev-web: dev-env ## Run the SvelteKit dev server on http://localhost:5173
	@set -a; source .env.dev; set +a; cd $(WEB_DIR) && $(PNPM) dev --host

migrate: .env ## Run database migrations (DbUp) using the api image
	$(COMPOSE) run --rm migrate

##@ Stack (Docker Compose, production-like)

up: .env ## Build/pull as needed and start db + migrate + api + web
	$(COMPOSE) up -d --remove-orphans
	@$(MAKE) ps

down: ## Stop and remove containers (data volume is kept)
	$(COMPOSE) --profile tunnel down --remove-orphans

logs: ## Tail logs of all services (S=api to filter)
	$(COMPOSE) --profile tunnel logs -f --tail=100 $(S)

ps: ## Show service status and health
	$(COMPOSE) --profile tunnel ps

build: ## Build api + web images locally (slow on a Pi; prefer `make pull`)
	$(COMPOSE) build --pull api web

pull: ## Pull the latest published images from GHCR
	$(COMPOSE) --profile tunnel pull --ignore-buildable

tunnel-up: .env ## Start everything including the Cloudflare tunnel
	$(COMPOSE) --profile tunnel up -d --remove-orphans
	@$(MAKE) ps

tunnel-down: ## Stop only the tunnel container
	$(COMPOSE) --profile tunnel stop cloudflared

pi: ## Raspberry Pi deploy/update: pull images, (re)start stack with tunnel
	@$(MAKE) pull
	@$(MAKE) tunnel-up
	@docker image prune -f >/dev/null && echo 'Pruned old images.'

##@ Data

backup: ## pg_dump → backups/couplescash-<timestamp>.sql.gz (KEEP=14 rotation)
	@scripts/backup.sh

restore: ## Restore a dump: make restore FILE=backups/couplescash-….sql.gz
	@[ -n "$(FILE)" ] || { echo 'Usage: make restore FILE=backups/couplescash-YYYYmmdd-HHMMSS.sql.gz'; exit 2; }
	@[ -f "$(FILE)" ] || { echo "No such file: $(FILE)"; exit 2; }
	@echo "About to restore $(FILE) into the live database (api + web will be stopped)."; \
	read -r -p 'Type "yes" to continue: ' ans; [ "$$ans" = yes ] || { echo aborted; exit 1; }
	$(COMPOSE) stop api web
	gunzip -c "$(FILE)" | $(COMPOSE) exec -T $(DB_SERVICE) psql -v ON_ERROR_STOP=1 -q -U $(DB_USER) -d $(DB_NAME)
	$(COMPOSE) start api web
	@echo 'Restore complete.'

##@ Quality

test: test-api test-web ## Run all tests (dotnet test + Playwright e2e)

test-api: ## dotnet test
	$(DOTNET) test $(API_SLN)

test-web: ## Playwright e2e (installs chromium on first run)
	cd $(WEB_DIR) && $(PNPM) exec playwright install chromium && $(PNPM) exec playwright test

lint: ## dotnet format --verify-no-changes + pnpm check + pnpm lint
	$(DOTNET) format $(API_SLN) --verify-no-changes
	cd $(WEB_DIR) && $(PNPM) check && $(PNPM) lint

fmt: ## Auto-format C# (dotnet format) and web (prettier)
	$(DOTNET) format $(API_SLN)
	cd $(WEB_DIR) && $(PNPM) format

clean: ## Remove build artefacts (bin/obj, .svelte-kit, build, reports). Keeps node_modules and data
	find $(API_DIR) -type d \( -name bin -o -name obj -o -name TestResults \) -prune -exec rm -rf {} +
	rm -rf $(WEB_DIR)/.svelte-kit $(WEB_DIR)/build $(WEB_DIR)/playwright-report $(WEB_DIR)/test-results
