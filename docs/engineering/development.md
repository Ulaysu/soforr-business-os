# Development Guide

Architecture rules: [architecture.md](../architecture/architecture.md). Agent roles and lifecycle: [AGENTS.md](../../AGENTS.md).

## Agent Task Lifecycle

```text
Backlog → Ready → Agent claims task → Inspect → Plan → Implement → Test
→ Review → Security check → Verification → Pull Request → Human approval → Merge → Done
```

- Agents take the **highest-priority Ready item whose dependencies are Done** from [backlog.md](../product/backlog.md).
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
- Errors are RFC 9457 `ProblemDetails` with a stable `code`. Validation errors list field problems.
- Lists are paginated with a bounded page size. Mutating endpoints that must be retry-safe accept an idempotency key.
- Breaking changes require approval and a new version.

## Error Handling

- Expected failures (validation, not found, rule violations) return typed results from handlers; no exceptions for control flow.
- Unexpected exceptions are handled once by middleware: log with correlation ID, return generic `ProblemDetails`. No stack traces, connection strings or SQL in responses.

## Logging and Observability

- Serilog structured logging; a correlation ID on every request, propagated to logs.
- Log business events (commands executed, actor, organization, outcome). Never log secrets, tokens, passwords or full personal data.
- Health checks (`/health/live`, `/health/ready`) before any deployment.
- Audit events for important business actions (e.g. reorder approved by user X).
- Later: metrics/tracing via OpenTelemetry, automation run history.

## Configuration

- `appsettings.json` holds non-secret defaults; secrets come from user-secrets/environment/secret store. Options bound to typed classes and validated at startup.

## Database Migrations

- EF Core migrations only; committed with the change that needs them; never edited after merge (add a new one).
- Every migration is reviewed for: existing data, nullable→non-null steps, indexes (lead with `OrganizationId`), FKs, constraints, locking impact.
- Migrations must apply cleanly to an empty database and to the previous release schema (verified in CI once the database exists).
- Destructive migrations (drop column/table) and major redesigns require human approval.
- No manual production schema changes.

## Frontend (Soforr.Web)

Vue 3 `<script setup>` + TypeScript strict, Vite, Tailwind. API access through a typed client generated from or aligned with the OpenAPI document. Components display and collect; they do not decide business outcomes.

## Local Development

Prerequisites: .NET 10 SDK, Docker (PostgreSQL via `docker compose`, added by FOUNDATION-003), Node LTS (from FOUNDATION-007).

```bash
dotnet restore
dotnet build
dotnet test
dotnet format --verify-no-changes
```
