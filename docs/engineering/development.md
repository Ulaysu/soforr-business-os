# Development Guide

Architecture rules: [architecture.md](../architecture/architecture.md). Agent roles and lifecycle: [AGENTS.md](../../AGENTS.md).

## Agent Task Lifecycle

```text
Backlog → Ready → Agent claims task → Inspect → Plan → Implement → Test
→ Review → Security check → Verification → Pull Request → Human approval → Merge → Done
```

- Agents take the **highest-priority Ready item whose dependencies are Done** from [backlog.md](../product/backlog.md): **SELLABLE-P0** before PRODUCT-P1 before FUTURE, and within SELLABLE-P0 in the [sequence to Sellable V1](../product/backlog.md#sequence-to-sellable-v1). Sellability is the sequencing constraint; quality is never traded for speed (cut scope, not corners).
- Claiming = set the item's status to `In Progress` (in the backlog, or the linked GitHub issue) and work on a branch `feat/<ID>-slug` (or `fix/`, `chore/`, `docs/`).
- Missing work discovered mid-task is documented and proposed as a new backlog item; continue only if it is required to finish the current task safely.
- Nothing merges without human approval of the pull request.

## Human Approval Boundaries

Agents **stop and ask** before:

- major architecture changes
- authentication architecture changes
- tenant-isolation changes
- major database redesign
- breaking API changes
- introducing microservices
- production deployment
- destructive operations (data deletion, force-push, history rewrite)
- paid external services
- automatic financial transactions
- major product-scope changes

Routine implementation that follows the documented architecture is autonomous.

## Web application

`src/Soforr.Web` (Vue 3, TypeScript, Vite, Tailwind, Vitest) has its own commands, run from that folder: `npm ci`, `npm run dev` (proxies to the API), `npm run lint`, `npm run format:check`, `npm run build`, `npm test`. CI runs them in the `web` job. Verify that the generated types are current with `npm run api:types:check` (CI regenerates them and fails on any difference). Details, the typed API client and how to regenerate the OpenAPI snapshot: [src/Soforr.Web/README.md](../../src/Soforr.Web/README.md). The client's types are generated from a committed snapshot of the API's OpenAPI document and a .NET test fails when the snapshot is stale, so an API change must be committed together with the regenerated snapshot and types.

## Secret scanning and vulnerable packages

CI scans the git history for secrets (gitleaks) and fails the build on known high or critical vulnerabilities in NuGet and npm packages. To check the same things before pushing:

```bash
dotnet restore Soforr.BusinessOS.slnx            # NuGet audit: NU1903 / NU1904 fail the restore
(cd src/Soforr.Web && npm audit --audit-level=high)
docker run --rm -v "$PWD:/repo" zricethezav/gitleaks:v8.24.3 detect --source /repo --config /repo/.gitleaks.toml --redact --no-banner
```

An optional pre-commit hook that scans only what is about to be committed (needs the gitleaks binary installed): create `.git/hooks/pre-commit` containing `gitleaks protect --staged --config .gitleaks.toml --redact` and make it executable. It is a convenience, not a requirement; CI is the check that counts. A false positive is handled by a narrow, explained entry in `.gitleaks.toml`, reviewed like any security change; never by weakening the rules.

## Repository Conventions

- Branches from `main`; small focused commits with conventional prefixes (`feat:`, `fix:`, `test:`, `docs:`, `chore:`, `refactor:`).
- One backlog item per pull request where practical.
- Never commit secrets. Local secrets via user-secrets or environment variables; `.env` is git-ignored.

## Coding Standards

- C# 14 / .NET 10, nullable reference types enabled, implicit usings on.
- Follow the surrounding style; `.editorconfig` (FOUNDATION-001) is authoritative once present. `dotnet format` must pass.
- Prefer `record` for commands, queries, DTOs and value objects; immutability by default.
- File-scoped namespaces. One public type per file. Names reflect the ubiquitous language of [modules.md](../architecture/modules.md).
- Async all the way, with `CancellationToken` on I/O methods.
- No comments that restate code; comment the *why*.
- No new NuGet/npm dependency without justification; major dependencies need approval (CLAUDE.md §21).

## Dependency Rules

See [architecture.md](../architecture/architecture.md#dependency-direction). Enforced by architecture tests (FOUNDATION-006) that fail the build on illegal project/namespace references.

## API Conventions

- REST, JSON, OpenAPI documented; routes under `/api/v1/...`; resource-oriented nouns, plural.
- Request/response models are separate from domain entities.
- Status codes: 200/201 (with `Location`)/204, 400 validation, 401 unauthenticated, 403 unauthorized, 404 not found **(also for other tenants' resources, to avoid leaking existence)**, 409 conflict/concurrency, 422 business-rule violation, 500 unexpected.
- Errors are RFC 9457 `application/problem+json`, written once, centrally. Every problem response carries `correlationId`; every error returned by a handler also carries a stable machine-readable `code` (for example `organization.not_found`), and clients should rely on `code`, not on the message.
  - Handler errors map by `ErrorKind` (`Soforr.Api.Http.ResultHttpExtensions`): Validation 400, NotFound 404, Forbidden 403, Conflict 409, RuleViolation 422. A Validation error is a validation problem whose `errors` object lists the message under the input field it is about (camelCase, for example `errors.name`); a failure that is not about one field is listed under the empty key.
  - Unhandled exceptions return a generic 500 (FOUNDATION-005).
  - A request the framework cannot read (for example a malformed JSON body) is a 400 problem in every environment. Minimal APIs throw `BadHttpRequestException` for it in Development; `ClientErrorExceptionHandler` turns it into the 400 it carries, without echoing the exception message.
- Lists are paginated with a bounded page size. Mutating endpoints that must be retry-safe accept an idempotency key.
- Breaking changes require approval and a new version.

## Error Handling

- Expected failures (validation, not found, rule violations) return `Result<T>` errors from handlers; no exceptions for control flow. Domain invariant violations are thrown as `DomainException` (with a stable `Code`) by the Domain and translated to an `Error` by the handler through `DomainErrors.Run`; see the handler pattern in [architecture.md](../architecture/architecture.md#module-layout-and-handler-pattern).
- Unexpected exceptions are handled once, by `UseApiPipeline` (exception handler + status-code pages + `AddApiErrorHandling`): the exception is logged with its correlation ID, and the client receives a generic RFC 9457 `application/problem+json` response (`500`, title "An unexpected error occurred.", no detail, no exception or stack trace, in every environment including Development) carrying `correlationId`. Errors from unknown routes (404) and other status codes use the same format.
- No stack traces, connection strings or SQL in responses.

## Logging and Observability

- Serilog structured logging. Levels and overrides live in the `Serilog` configuration section; the console sink is human-readable in Development and compact structured JSON (`RenderedCompactJsonFormatter`) in every other environment. Configured in `Soforr.Api.Hosting.ApiHostingExtensions.UseApiLogging`.
- **Correlation ID:** every request has one. A caller-supplied `X-Correlation-ID` is honored only if it is at most 64 characters of letters, digits, `-`, `_` or `.`; otherwise a new one is generated. It is returned in the `X-Correlation-ID` response header, attached to every log event of the request as `CorrelationId`, and included in problem responses as `correlationId`.
- **Request logging:** one completion event per request (method, path, status, duration). Query strings, headers and bodies are not logged.
- Log business events (commands executed, actor, organization, outcome). Never log secrets, tokens, passwords or full personal data.
- Health checks: `GET /health/live` (process is up; no dependency checks) and `GET /health/ready` (database reachable). Both return only `Healthy`/`Unhealthy`, 200 or 503, with no details. They are unauthenticated for now; authorization policy arrives with IDENTITY-002.
- Audit events for important business actions (e.g. reorder approved by user X).
- Later: metrics/tracing via OpenTelemetry, automation run history.

## Configuration

Configuration is bound to typed options classes and validated when the host starts, so a missing or invalid setting stops the application immediately with a message naming the problem instead of failing later at runtime.

**Sources and precedence** (later sources override earlier ones; standard ASP.NET Core behavior):

1. `appsettings.json`: non-secret defaults, tracked in Git.
2. `appsettings.{Environment}.json`: non-secret, environment-specific values, tracked in Git.
3. User secrets (Development only): local secrets, stored outside the repository.
4. Environment variables: how secrets and environment values are supplied in every other environment. Use `__` as the section separator, e.g. `ConnectionStrings__Database`.
5. Command-line arguments.

**Rules**

- No secret (password, token, API key, connection string with credentials, signing key) in any tracked file, including `appsettings*.json`, Dockerfiles, compose files, tests, and docs. Tracked files may contain placeholders or non-secret defaults only.
- Local development secrets use user secrets (the Api project has a `UserSecretsId`):

  ```bash
  dotnet user-secrets set "ConnectionStrings:Database" "<value>" --project src/Soforr.Api
  dotnet user-secrets list --project src/Soforr.Api
  ```

- `.env` files are git-ignored and are for tools that read them (e.g. Docker Compose from FOUNDATION-003). ASP.NET Core does not load `.env` by itself.
- Non-development secrets come from the deployment environment's secret store; never from files in the image or repository.
- Never log configuration values that may contain secrets.
- Rotate any secret that was committed, even briefly.

**Adding a setting**

1. Create an options class (in the layer that consumes it; Infrastructure options live in Infrastructure) with data annotations for required values and ranges.
2. Register it with `services.AddValidatedOptions<TOptions>(configuration, "SectionName")` (`Soforr.Infrastructure.Configuration`). This binds the required section, validates annotations, and validates at startup. A missing section fails with `Section 'X' not found in configuration.`; a missing or invalid value fails with `DataAnnotation validation failed for 'TOptions' members: ...`.
3. Consume it through `IOptions<TOptions>`; do not read `IConfiguration` directly in application code.
4. Add the key to the table below.

**Configuration keys**

| Key | Required | Secret | Description |
|---|---|---|---|
| `ConnectionStrings:Database` | yes | **yes** | PostgreSQL connection string (Npgsql format). Application fails at startup if missing. |
| `Session:IdleTimeout` | no | no | Sliding idle timeout of the sign-in cookie. Default `01:00:00`. |
| `Session:AbsoluteLifetime` | no | no | Longest a session can last however active. Default `12:00:00`. |
| `Session:SecurityStampValidationInterval` | no | no | How often a session is re-validated against the security stamp and membership. Default `00:01:00`. |

## Database Migrations

- EF Core migrations only; committed with the change that needs them; never edited after merge (add a new one).
- Every migration is reviewed for: existing data, nullable→non-null steps, indexes (lead with `OrganizationId`), FKs, constraints, locking impact.
- Migrations must apply cleanly to an empty database and to the previous release schema (verified in CI once the database exists).
- Migrations live in `src/Soforr.Infrastructure/Persistence/Migrations` and are marked as generated code in `.editorconfig` (excluded from formatting and analyzers). The `dotnet-ef` tool is pinned in `.config/dotnet-tools.json`; run `dotnet tool restore` once.
- The application never migrates the database on startup. Apply migrations explicitly:

  ```bash
  dotnet ef migrations add <Name> --project src/Soforr.Infrastructure --startup-project src/Soforr.Api --output-dir Persistence/Migrations
  dotnet ef database update --project src/Soforr.Infrastructure --startup-project src/Soforr.Api
  ```

  The `ef` commands start the Api host to discover the context, so `ConnectionStrings:Database` must be available (user secrets in Development, or the `ConnectionStrings__Database` environment variable). `migrations add` does not connect to the database; `database update` does.
- Destructive migrations (drop column/table) and major redesigns require human approval.
- No manual production schema changes.

## Authentication in development

The authentication endpoints exist only in Development (see [ADR 0002](../decisions/0002-authentication.md)). The session and antiforgery cookies are `Secure`, so use the `https` launch profile (`dotnet run --project src/Soforr.Api --launch-profile https`; trust the development certificate once with `dotnet dev-certs https --trust`). A client signs in like this:

1. `GET /api/v1/auth/antiforgery` returns `{ "headerName": "X-CSRF-TOKEN", "token": "..." }` and sets the antiforgery cookie.
2. `POST /api/v1/auth/register` (or `/login`) with that header. Registration creates the user, a new organization and the Owner membership; it does not sign in.
3. The token is bound to the identity it was issued for: fetch a new one after signing in or out, before the next unsafe request.
4. `GET /api/v1/auth/me` returns the user, organization and role, or `401`.
5. Every other endpoint requires the session cookie (`401` otherwise) and, for unsafe methods, the antiforgery header. `403` means signed in but not allowed; a resource of another organization is `404`.

## Frontend (Soforr.Web)

Vue 3 `<script setup>` + TypeScript strict, Vite, Tailwind. API access through a typed client generated from or aligned with the OpenAPI document. Components display and collect; they do not decide business outcomes.

## Local Development

Prerequisites: .NET 10 SDK, Docker (local PostgreSQL via `docker compose`), Node LTS (from FOUNDATION-007).

```bash
dotnet restore
dotnet build
dotnet test
dotnet format --verify-no-changes
dotnet test --filter "Category!=Integration"   # without Docker
```

`dotnet test` runs the integration tests too, which start a PostgreSQL container and therefore need Docker (see [testing.md](testing.md#postgresql-integration-harness)).

**Local database**

```bash
cp .env.example .env            # then set your own POSTGRES_PASSWORD in .env (git-ignored)
docker compose up -d            # PostgreSQL 17 on 127.0.0.1:5432 (loopback only)
dotnet user-secrets set "ConnectionStrings:Database" "Host=localhost;Port=5432;Database=soforr;Username=soforr;Password=<your POSTGRES_PASSWORD>" --project src/Soforr.Api
dotnet tool restore
dotnet ef database update --project src/Soforr.Infrastructure --startup-project src/Soforr.Api
dotnet run --project src/Soforr.Api --launch-profile http
```

`GET /health/ready` reports database status (200 healthy, 503 unhealthy; no details are exposed). Data lives in the `postgres-data` Docker volume; `docker compose down -v` deletes it. The compose file is for local development only.

**The whole system in containers (DEPLOY-001)**

To run PostgreSQL, the migrations and the API as containers (no .NET SDK needed to run, only to create the certificate):

```bash
cp .env.example .env            # set POSTGRES_PASSWORD and DEV_CERT_PASSWORD (git-ignored)
mkdir -p .local/certs
dotnet dev-certs https -ep .local/certs/aspnetapp.pfx -p "<your DEV_CERT_PASSWORD>"   # git-ignored; on Linux also: chmod 644 .local/certs/aspnetapp.pfx
docker compose --profile app up --build
```

Then `https://localhost:7443/health/ready` answers 200 (the browser warns about the development certificate unless you trust it with `dotnet dev-certs https --trust`), and `https://localhost:7443/openapi/v1.json` is the API contract. What it does and why:

- `postgres` is the same service as above. `migrate` runs once from the same image build (an EF Core migration bundle of the committed migrations) and exits: the application still never migrates itself. `api` starts only after the migrations finished.
- The API runs in **Development** mode, because the authentication endpoints and the OpenAPI document exist only there until e-mail verification (IDENTITY-004) is done. **This is not a deployable configuration** and is bound to loopback only; production deployment is DEPLOY-002.
- HTTPS is required because the session and antiforgery cookies are `Secure`; the certificate is mounted read-only from `.local/certs` and never baked into an image. The connection string and passwords come from `.env`, never from the image.
- The API container runs as the image's non-root user. Data protection keys are not persisted, so a sign-in does not survive `docker compose restart api`; sign in again.
- `docker compose --profile app down -v` stops everything and deletes the database volume. CI builds and starts exactly this setup and checks it (the `containers` job).
- To use the web dev server with it: `SOFORR_API_URL=https://localhost:7443 npm run dev` in `src/Soforr.Web`.
