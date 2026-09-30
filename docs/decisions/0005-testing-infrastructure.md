# 0005 — Testing infrastructure

- Status: **Accepted** (approved by the project owner, 2026-09-30)
- Date: 2026-09-30
- Deciders: project owner

## Context

CLAUDE.md §19 prioritises tests for inventory, sales, purchases, permissions and tenant isolation, and says not to add tests (or tooling) for their own sake. Much of our critical behavior depends on PostgreSQL semantics: concurrency tokens, CHECK constraints, unique indexes, transactions, query filters, migrations. `dotnet test` on the current solution passes vacuously because no test project exists.

## Evaluation

| Tool | Value here | Decision |
|---|---|---|
| **xUnit** | Approved in CLAUDE.md | Use (current major shipped by `dotnet new xunit`) |
| **Domain/Application unit tests** | Invariants, calculations, forecasting; no I/O | Use; no extra packages; hand-written fakes |
| **`Microsoft.AspNetCore.Mvc.Testing`** (`WebApplicationFactory`) | First-party; API workflows, auth, error contract | Use |
| **PostgreSQL integration tests on a real database** | Only way to verify xmin tokens, constraints, filters, migrations. EF InMemory/SQLite would give false confidence | **Required** |
| **Testcontainers (`Testcontainers.PostgreSql`)** | `dotnet test` starts its own throwaway PostgreSQL; same in CI and locally; needs Docker | **Use** (one test-only package). Alternative without a package: point tests at the compose database or a CI `services: postgres`. It saves a package but requires manual setup and env plumbing. Chosen for zero-setup reliability; revisit if Docker-in-CI becomes a problem |
| **Architecture tests** | Guard ADR 0001 dependency rules | Start with **plain reflection over assembly references** (no package). Add `NetArchTest.Rules` only when a type-level rule (e.g. endpoints must not use Infrastructure types) cannot be expressed otherwise |
| **End-to-end (Playwright)** | Critical journeys through the UI | **Defer** until the first real UI flow (CATALOG-004); then one journey, expanded slice by slice |
| Frontend unit tests (Vitest) | Comes with the Vue scaffold (FOUNDATION-007) | Use |
| FluentAssertions, Moq/NSubstitute, AutoFixture, Bogus, Respawn, coverage tools | Convenience, not need (FluentAssertions v8+ is commercial) | **Do not add** now; xUnit `Assert` and hand-written fakes suffice |

## Decision: the smallest useful stack

```text
tests/
├── Soforr.Domain.Tests              xunit
├── Soforr.Application.Tests         xunit (+ real-DB tests via the integration fixture where LINQ matters)
├── Soforr.Architecture.Tests        xunit + reflection (no extra package)
└── Soforr.Api.IntegrationTests      xunit + Mvc.Testing + Testcontainers.PostgreSql
```

Test-only packages added in total: `xunit` family + `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`. Nothing else without justification in the PR.

Rules:
- **One PostgreSQL container per test run** (shared collection fixture); **one database per test class** created from migrations (also proves migrations apply to an empty database). No cross-test data sharing, no test ordering dependence.
- Tests category-tagged (`Integration`) so the fast loop can exclude them: `dotnet test --filter "Category!=Integration"`.
- Time and ids injected (`IClock`), so tests are deterministic.
- Two-tenant fixture is part of the integration harness (used by IDENTITY-003 onward).
- AI model calls are always faked in tests.
- Never weaken or delete a test to pass a build.

## Sequencing (backlog impact)

- FOUNDATION-006: unit test projects, architecture tests, API test project skeleton (`WebApplicationFactory`, no database). Depends only on FOUNDATION-002.
- FOUNDATION-008 (new): PostgreSQL Testcontainers harness, database-per-class, migration apply test. Depends on FOUNDATION-003 and 006.

## Consequences

- Contributors and CI need Docker for integration tests (already needed for local PostgreSQL). GitHub-hosted Ubuntu runners include it.
- Slightly slower integration tests than in-memory, in exchange for real behavior.
- Two small test-only dependencies beyond xUnit; both are widely used and replaceable.
