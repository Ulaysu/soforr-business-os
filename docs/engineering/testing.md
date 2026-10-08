# Testing Strategy

Tests protect meaningful behavior; coverage numbers are not a goal. Test behavior, not implementation details.

## Layout

```text
tests/
├── Soforr.Domain.Tests           unit tests, no I/O
├── Soforr.Application.Tests      handler tests with fakes/in-memory ports
├── Soforr.Architecture.Tests     dependency-direction rules
└── Soforr.Api.IntegrationTests   WebApplicationFactory + real PostgreSQL (Testcontainers); see API test hosts and the PostgreSQL harness
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
3. Deterministic intelligence (inventory risk and coverage, sales comparisons, margins, purchasing cost, customer aging, and later reorder recommendations): deterministic outputs for fixed inputs and a fixed clock, edge cases (no sales history, zero velocity, period boundaries).
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

## PostgreSQL Integration Harness

Tests that need a real database use the harness in `tests/Soforr.Api.IntegrationTests/Postgres` ([ADR 0005](../decisions/0005-testing-infrastructure.md)). It needs Docker; the first run pulls the `postgres:17` image (the same image as `docker-compose.yml`).

- **One container per test run.** `SharedPostgresContainer` starts PostgreSQL (Testcontainers) on first use and shares it between all test classes; it is removed when the test process exits.
- **One database per test class.** `PostgresDatabaseFixture` creates a new, empty database, checks that it really is empty, applies the application's migrations with `MigrateAsync`, and drops the database when the class finishes. Using it therefore also proves that the migrations apply to an empty database. Because each class owns its database, classes run in parallel and in any order without sharing data. Tests inside one class do share that class's database, so they must create the data they need and not assume empty tables.
- **Using it:**

  ```csharp
  [Trait("Category", "Integration")]
  public class ProductPersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
  {
      [Fact]
      public async Task Something()
      {
          await using var context = database.CreateContext();               // AppDbContext on this class's database
          using var factory = new ApiFactory(database.ConnectionString);    // the real application on the same database
          // ...
      }
  }
  ```

- **Handlers.** Command and query handlers are tested on this harness (see `Organizations/OrganizationHandlerTests`): construct the handler with `database.CreateContext()`, a fixed `IClock` and predictable ids, then assert both the returned `Result` and what is stored. The real database matters because the handlers use LINQ and persistence.
- **Authentication.** `Identity/AuthApiTests` drives the real application with a cookie-keeping https client: register, sign-in, sign-out, lockout, antiforgery, the absolute session lifetime and revocation. Settings such as `Session:AbsoluteLifetime` are overridden per test with `ConfigureAppConfiguration`, so the absolute-lifetime test waits seconds, not hours; the revocation tests set `Session:SecurityStampValidationInterval` to zero.
- **Authorization.** `Organizations/OrganizationAuthorizationTests` is the matrix: anonymous, Owner, Manager, Staff and a member of another organization against every operation, through the real application. `AuthClient` signs in test users; a Manager or Staff user is made by moving a registered user's membership, because members cannot be invited yet. `EndpointConventionTests` (default-deny fallback, the anonymous allowlist, antiforgery on every unsafe endpoint) and the architecture test `AuthorizationConventionTests` (every handler takes `ICurrentUser`) guard new endpoints and handlers.
- **Persistence tests.** `Postgres/ProductPersistenceTests` is the pattern for an entity: a full round trip, constraint tests that write raw SQL (so the database is proven to reject bad data even when the Domain is bypassed), the unique-rule tests including concurrent inserts, and a column-type test. Owned value objects are covered by their owner's tenant filter in `TenantConventionTests`.
- **Audit.** A use case that records an audit event is tested for three things: the event exists after success, no event exists after a failure (it was part of the failed unit of work), and the event holds no personal data. `Audit/AuditApiTests` shows the pattern; `Postgres/AuditPersistenceTests` proves the append-only rule at the database with raw SQL.
- **Stock invariants.** After any concurrent or randomized scenario, run the reconciliation query (`InventoryPersistenceTests.ReconciliationMismatchesAsync`): it must return no stock level that differs from the sum of its movements. `StockMovementRecorderTests` shows the pattern: forced races with the save interceptor, real parallel requests for the outcome, and the reconciliation after each.
- **Races.** To test the path where a competing request wins between a handler's check and its save, do not rely on concurrency luck: build the context with a `SaveChangesInterceptor` that performs the competing write just before the save (see `Catalog/ProductHandlerTests`). A concurrent test is still worth having, but it cannot prove that a particular path ran.
- **Intelligence.** Rules and calculators are pure, so test them without a database: typed fixtures, a fixed clock, and the edge cases (no history, zero velocity, period boundaries, the organization's time zone, voided sales, missing cost). Add an integration test that the gathering query returns what the rule needs and that results reconcile with the raw records, plus authorization (`ProductViewCost` for cost-derived output) and tenant isolation. The same data and clock must always give the same insight.
- **Category.** Tag every class that uses the harness `[Trait("Category", "Integration")]`. To run everything except these tests, for example without Docker: `dotnet test --filter "Category!=Integration"`.
- **Two tenants.** `TwoTenants` creates two organizations, each with a signed-in Owner and a client of their own, through the real application. Every tenant-owned entity gets cases in `TenantIsolation/TenantIsolationTests` (reads, writes, deletes, creates, foreign and missing ids, ids in route and body); `TenantConventionTests` fails when an entity has no tenant filter. `PostgresDatabaseFixture.CreateContext(organizationId)` gives a context acting for an organization; without one the filters match nothing, so tests that verify stored data across tenants read with `IgnoreQueryFilters()`.
- **No Docker.** If the container cannot be started, the tests fail with a message saying Docker is required; they are never skipped silently.
- **Without Docker.** Set `SOFORR_TEST_POSTGRES` to an administrative connection string of a PostgreSQL 17 server (for example a throwaway cluster made with `initdb` and `pg_ctl`) and the harness uses it instead of a container; each test class still gets and drops its own database. CI does not set it and keeps using the container.
- **Docker endpoint.** Testcontainers honors the standard `DOCKER_HOST` variable. If it cannot find your Docker engine although the `docker` CLI works, point it at the engine the CLI uses (see `docker context inspect --format '{{.Endpoints.docker.Host}}'`).

## Offline resilience tests (ADR 0007)

When the offline work lands, these are required, in addition to the usual pyramid:

- **Server (PostgreSQL integration):** replay of every replayable command (sent once, twice, five times, and again after the response was lost: exactly one business effect, same answer shape); the offline authorization boundary and the bounds of `occurredAtUtc` (including a backdated request that is inside the age window but outside any authorization, and an invalid offline time that must never fall back to server time); `catalog_price_changed`; insufficient stock on replay leaves nothing behind; tenant isolation on replay; the error taxonomy used by the client to decide retry or conflict.
- **Client (Vitest with an IndexedDB implementation for tests):** persistence across reopen, durable-before-saved, ordering and dependencies, failure classification and backoff with a fake clock, one database per user and organization, schema migration of old stored operations, bounded cache limits, sign-out blocking.
- **Real browser:** an automated offline journey (cache, go offline, sell, refresh, restart, reconnect, verify one sale, stock and audit on the server) and the lost-response scenario. Playwright is approved for this (ADR 0007, OFFLINE-007) and is added when the web and end-to-end infrastructure is built.

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
