# CouplesCash API

.NET 10 Minimal API backing the CouplesCash PWA. Implements [`docs/API-CONTRACT.md`](../../docs/API-CONTRACT.md)
on PostgreSQL 16 with Npgsql + Dapper, DbUp migrations, Argon2id passwords, HS256 JWTs with rotating
refresh tokens and Scalar docs.

```
apps/api/
├─ CouplesCash.slnx
├─ Directory.Build.props          # net10.0, nullable, warnings-as-errors, invariant globalization
├─ Dockerfile / .dockerignore     # multi-stage, multi-arch, non-root (USER app), port 8080
├─ CouplesCash.Api.http           # VS Code REST Client walkthrough
├─ scripts/smoke.sh               # curl + jq end-to-end smoke test
├─ CouplesCash.Api/
│  ├─ Program.cs                  # host, DI, pipeline, --migrate
│  ├─ Endpoints/                  # one static class per area, MapGroup("/v1")
│  ├─ Auth/                       # Argon2idPasswordHasher, TokenService, CoupleCodeService, JwtOptions
│  ├─ Domain/                     # enums, RecurrenceEngine (pure)
│  ├─ Data/                       # NpgsqlDataSource, Dapper repositories, DbUpMigrator
│  ├─ Services/                   # AuthSessionService, OccurrenceService, DTO mapping
│  ├─ Validation/                 # FluentValidation validators + endpoint filter
│  ├─ Infrastructure/             # ProblemDetails, exception handler, rate limiting, OpenAPI bearer, options
│  └─ Migrations/0001_initial.sql # embedded, journaled in public.schema_versions
└─ CouplesCash.Api.Tests/         # xunit: recurrence, couple codes, tokens, Argon2
```

## Run locally

Prerequisites: .NET 10 SDK and a PostgreSQL 16 database (`CREATE DATABASE couplescash;` — the
`citext` extension is created by the first migration, so the DB user needs `CREATE` on the database
or the extension must already exist).

```bash
# Development defaults (appsettings.Development.json) expect:
#   Host=localhost;Port=5432;Database=couplescash;Username=couplescash;Password=couplescash
# Override anything with env vars (double underscore = section separator):
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=couplescash;Username=postgres"
export Jwt__Secret="$(openssl rand -base64 48)"       # optional in Development (a dev default is used with a warning)

dotnet run --project CouplesCash.Api                   # migrates on startup, listens on http://localhost:8080
```

- Health: `GET http://localhost:8080/health`
- Docs: `http://localhost:8080/scalar` and `http://localhost:8080/openapi/v1.json` (Development, or `Api__EnableDocs=true`)

## Migrate only

```bash
dotnet run --project CouplesCash.Api -- --migrate      # exit code 0 = schema up to date
# or from the published output / container:
dotnet CouplesCash.Api.dll --migrate
```

Startup migration can be disabled with `Database__MigrateOnStartup=false` (then run `--migrate` as a one-off job).

## Test, format, build

```bash
dotnet build -warnaserror
dotnet test
dotnet format --verify-no-changes
scripts/smoke.sh http://localhost:8080                 # needs a running API + curl + jq
```

## Configuration

| Setting (env form) | Default | Notes |
|---|---|---|
| `ConnectionStrings__Default` | — (required) | Npgsql connection string |
| `Jwt__Secret` | — | ≥ 32 bytes. **Required outside Development**; startup fails otherwise |
| `Jwt__AccessMinutes` | `15` | Access token lifetime |
| `Jwt__RefreshDays` | `30` | Refresh token lifetime |
| `Api__EnableDocs` | `false` | Serve `/scalar` + `/openapi/v1.json` (always on in Development) |
| `Api__AllowedOrigins` | empty | CORS origins, comma-separated or indexed (`Api__AllowedOrigins__0`). Empty = CORS off (the BFF talks server-to-server) |
| `Database__MigrateOnStartup` | `true` | Apply pending migrations before listening |
| `RateLimiting__AuthPerMinute` | `10` | Fixed window per IP for credential endpoints and `/v1/couple/join` |
| `RateLimiting__GlobalPerMinute` | `120` | Fixed window per IP for everything else |
| `ASPNETCORE_URLS` | `http://+:8080` | Listening address |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables docs, readable logs and the dev JWT secret |

Logging is Serilog to stdout: human-readable in Development, compact JSON (one event per line) otherwise.
Log levels come from the `Serilog` section of `appsettings.json`.

## Docker

```bash
docker build -t couplescash-api apps/api                       # context = apps/api
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__Default="Host=db;Database=couplescash;Username=couplescash;Password=..." \
  -e Jwt__Secret="$(openssl rand -base64 48)" \
  couplescash-api
```

The image runs as the non-root `app` user, needs no writable filesystem (`read_only: true` is fine)
and has no `HEALTHCHECK` (the runtime image ships without curl/wget); use a compose healthcheck
against `GET /health`.

## Behaviour notes

- Household scoping on every data route comes from the `hh` JWT claim; after `POST /v1/couple/code`
  (which creates the household on first use) the client must call `/v1/auth/refresh`. `join` and
  `leave` return a fresh `AuthResult` directly.
- `/v1/couple/*` reads membership from the database so it works with a stale token.
- Unknown or foreign `{id}` → `404` problem; validation → `400` with `errors: { field: [msg] }`;
  rate limited → `429` with `Retry-After`.
- Couple codes: `code` = first 40 bits of SHA-256(payload bytes) in Crockford base32, `XXXX-XXXX`.
  Both `sha256(payload bytes)` and `sha256("XXXXXXXX")` (upper-case, no dash) are stored; typed input
  is normalised (case, dashes/spaces, `O→0`, `I/L→1`).
- `PUT /recurring/{id}/occurrences/{date}/paid` returns 404 unless `{date}` is produced by the
  recurrence engine for that item (month-end clamping included).
- Summary: `dueThisMonth/paid/remaining` cover bills + subscriptions due in the month; `monthly*`,
  `byCategory`, `byKind` are monthly equivalents of items that have not ended before the month.
