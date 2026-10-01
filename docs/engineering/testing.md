# Testing Strategy

Tests protect meaningful behavior; coverage numbers are not a goal. Test behavior, not implementation details.

## Layout

```text
tests/
├── Soforr.Domain.Tests           unit tests, no I/O
├── Soforr.Application.Tests      handler tests with fakes/in-memory ports
├── Soforr.Architecture.Tests     dependency-direction rules
└── Soforr.Api.IntegrationTests   WebApplicationFactory (+ real PostgreSQL from FOUNDATION-008); see API test hosts
```

xUnit v2 throughout. Created in FOUNDATION-006. `Soforr.Domain.Tests` received its first tests with ORG-001 (Currency, Money, Organization). Architecture tests check both the references declared in each `.csproj` and the compiled assembly references, because the compiler omits references that no code uses.

## Pyramid

| Level | Scope | Runs on |
|---|---|---|
| Unit | Domain invariants, calculations, forecasting math | every build |
| Application | Handlers with fake ports: orchestration, authorization, validation | every build |
| Integration | EF Core, transactions, concurrency, migrations, auth, tenant isolation, API workflows against real PostgreSQL (Testcontainers; see [ADR 0005](../decisions/0005-testing-infrastructure.md)) | every PR |
| End-to-end | Critical journeys through the UI (Playwright), added once the Web project has flows | PR / nightly |
| Architecture | Layer/dependency rules | every build |

## Must-Have Coverage (priority order)

1. Inventory: movements, stock = sum of movements, no negative stock, concurrent sales/receipts.
2. Sales and purchases: totals, atomicity (failure leaves no partial state), idempotent receiving.
3. Reorder recommendations and forecasting: deterministic outputs for fixed inputs, edge cases (no sales history, zero velocity).
4. Authorization: each protected command/query rejects users without permission.
5. **Tenant isolation:** for every tenant-owned endpoint, a user from organization B cannot read/modify organization A's data (404, not 403 leak).
6. Financial calculations: rounding, currency, boundaries.

## API Test Hosts

The API tests use three kinds of host. Each has one job; pick the one that matches what the test must prove.

| Host | Use it for | Do not use it for |
|---|---|---|
| `WebApplicationFactory<Program>` (via `ApiFactory`) | **The default for API tests.** The real application as composed by `Program.cs`: routing, endpoint mapping, DI wiring, options, authentication and authorization, tenant isolation, API workflows, and (from FOUNDATION-008) the real PostgreSQL database. | Asserting that the host *fails to start* (see the practice below). |
| `PipelineTestHost` | Cross-cutting pipeline behavior in isolation: correlation ID handling, generic error responses, request logging. It runs the real `UseApiPipeline()`/`AddApiErrorHandling()` with test-only endpoints (for example one that throws) and an in-memory log sink, so behavior that no real endpoint can trigger yet can be asserted precisely. | Business endpoints, workflows, authorization, tenancy, database access, or anything that depends on how `Program.cs` wires things. |
| Plain generic host (`Host.CreateEmptyApplicationBuilder`) | Startup validation (`ValidateOnStart`, missing configuration) for a registration extension. | HTTP behavior. |

`PipelineTestHost` is a focused harness, **not a replacement for the `WebApplicationFactory` integration strategy** and it does not lower the bar for it:

- It proves the pipeline components behave correctly; it cannot prove the application uses them. That wiring is confirmed by `ApiFactory` tests against the real `Program` (for example `/health/live` carries `X-Correlation-ID`).
- New endpoints and workflows are tested through `ApiFactory`. Do not add real endpoints or business behavior to `PipelineTestHost`.
- When a cross-cutting concern needs authentication, tenant context or the database (for example logging the organization, or error mapping from real handlers), test it through `ApiFactory`.

## Practices

- Deterministic tests: inject `IClock` and id generators; no reliance on wall time or ordering.
- Integration tests use isolated databases/schemas per run and are not order-dependent.
- Each bug fix adds a regression test.
- Test names describe behavior (`Sale_WithInsufficientStock_IsRejected_AndLeavesStockUnchanged`).
- AI: tools are tested like any Application service; model calls are faked in tests; an evaluation set covers prompts (later).
- Do not weaken or delete a test to make a build pass; fix the cause or raise it.
- Startup-failure behavior (for example options validation) is tested with a plain generic host, not `WebApplicationFactory`: when the full application host fails to start, its deferred host intermittently reports an `ObjectDisposedException` instead of the real exception. See [API test hosts](#api-test-hosts).

## Commands

```bash
dotnet test                       # all
dotnet test --filter "Category!=Integration"   # fast loop
```
