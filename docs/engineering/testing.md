# Testing Strategy

Tests protect meaningful behavior; coverage numbers are not a goal. Test behavior, not implementation details.

## Layout

```text
tests/
├── Soforr.Domain.Tests           unit tests, no I/O
├── Soforr.Application.Tests      handler tests with fakes/in-memory ports
├── Soforr.Architecture.Tests     dependency-direction rules
└── Soforr.Api.IntegrationTests   WebApplicationFactory + real PostgreSQL
```

xUnit v2 throughout. Created in FOUNDATION-006. `Soforr.Domain.Tests` has no tests until the Domain contains behavior (ORG-001). Architecture tests check both the references declared in each `.csproj` and the compiled assembly references, because the compiler omits references that no code uses.

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

## Practices

- Deterministic tests: inject `IClock` and id generators; no reliance on wall time or ordering.
- Integration tests use isolated databases/schemas per run and are not order-dependent.
- Each bug fix adds a regression test.
- Test names describe behavior (`Sale_WithInsufficientStock_IsRejected_AndLeavesStockUnchanged`).
- AI: tools are tested like any Application service; model calls are faked in tests; an evaluation set covers prompts (later).
- Do not weaken or delete a test to make a build pass; fix the cause or raise it.
- Startup-failure behavior (for example options validation) is tested with a plain generic host, not `WebApplicationFactory`: when the full application host fails to start, its deferred host intermittently reports an `ObjectDisposedException` instead of the real exception. Cross-cutting HTTP behavior is tested with `PipelineTestHost`, which uses the real Api pipeline with test-only endpoints and an in-memory log sink.

## Commands

```bash
dotnet test                       # all
dotnet test --filter "Category!=Integration"   # fast loop
```
