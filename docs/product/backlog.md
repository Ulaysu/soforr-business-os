# Master Backlog

Agents work from this backlog. Take the **highest-priority `Ready` item whose dependencies are all `Done`**. Lifecycle and approval rules: [development.md](../engineering/development.md). Completion criteria: [definition-of-done.md](../engineering/definition-of-done.md). Phases: [roadmap.md](roadmap.md).

**Status values:** `Backlog` (not yet refined or dependencies open) · `Ready` (refined, dependencies Done) · `In Progress` · `In Review` · `Done`.
**Priority:** P0 (blocks the first slice) · P1 (needed for the first usable product) · P2 (important, not blocking).
**Suggested agents** refer to roles in [AGENTS.md](../../AGENTS.md).

Every item's testing requirements are additional to the universal Definition of Done. Items deliberately represent meaningful, verifiable units of work rather than tiny tasks.

## Architecture Gate

These ADRs in [docs/decisions/](../decisions/README.md) are **Accepted** (approved by the owner, 2026-09-30). The listed items must implement them as written; any deviation needs a new or superseding ADR and human approval.

| ADR | Governs |
|---|---|
| 0001 Dependency model | FOUNDATION-002, FOUNDATION-003 |
| 0005 Testing infrastructure | FOUNDATION-006, FOUNDATION-008 |
| 0003 Commands/queries without mediator | ORG-001 |
| 0002 Authentication, authorization, tenant isolation | IDENTITY-001 and everything after it |
| 0004 Inventory concurrency | INVENTORY-001 |

Not governed by an ADR: FOUNDATION-001, FOUNDATION-004, FOUNDATION-007, SECURITY-001.

## Dependency Graph

```text
Engineering Foundation (EPIC 01)
        ↓
Organizations (ORG)
        ↓
Authentication (IDENTITY-001)
        ↓
Authorization / Tenant Isolation (IDENTITY-002, -003)
        ↓
Product Catalog (CATALOG)
        ↓
Inventory (INVENTORY)
        ↓
Purchasing (PURCHASING)
        ↓
Sales (SALES)
        ↓
Business Intelligence (INTEL-001..002)
        ↓
Recommendations (INTEL-003)
        ↓
AI Assistant (EPIC 12)
```

**Safe parallel work**

- FOUNDATION-001, -002, -004, -007 and SECURITY-001 have no mutual dependencies.
- FOUNDATION-007 (Vue scaffold) runs in parallel with all backend work until the first UI item.
- Within an epic, API work and UI work run in parallel once the API contract is agreed (`*-UI` items depend on the API item).
- ORG-001 and CATALOG-001 (pure domain models, unit-tested) need no database or identity work and can proceed while FOUNDATION-003/005/008 and IDENTITY-* are in progress. Their persistence and API items cannot.
- FOUNDATION-003 and FOUNDATION-005 can run in parallel once FOUNDATION-004 and FOUNDATION-006 are Done.
- SECURITY-002 and AUDIT-001 can proceed alongside INVENTORY once IDENTITY-003 is Done.
- Purchasing and Sales both need Inventory but not each other; they may run in parallel after INVENTORY-003 (see notes on PURCHASING-001 and SALES-001).

---

# EPIC 01 — Engineering Foundation

### FOUNDATION-001 — Repository architecture and standards
- **Status:** Done · **Priority:** P0 · **Agent:** Architecture / Backend
- **Description:** Establish repo-wide build conventions: `global.json` pinning the .NET 10 SDK with `rollForward: latestFeature` (developer machines may also have .NET 9 installed; without a pin the SDK in use is implicit), `Directory.Build.props` (nullable, warnings, analyzers, `TreatWarningsAsErrors` for CI), `.editorconfig`, `.gitattributes`, and `Directory.Packages.props` for central package versions. Update `.github/workflows/ci.yml` to use `global-json-file` in `setup-dotnet` and add a `dotnet format --verify-no-changes` step.
- **Acceptance criteria:** `dotnet build` and `dotnet format --verify-no-changes` pass at the repo root; CI uses the same SDK as `global.json`; package versions live in one place; no behavior changes.
- **Dependencies:** none
- **Affected area:** repository root, all projects
- **Testing:** build + format verification; CI green (until FOUNDATION-006 the CI test step passes vacuously because there are no test projects).

### FOUNDATION-002 — Solution structure and composition roots
- **Status:** Done · **Priority:** P0 · **Agent:** Backend
- **Description:** The four backend projects already exist with the agreed references. Add per-layer registration entry points (`AddApplication()`, `AddInfrastructure(config)`), add a `tests/` solution folder, and wire the composition in `Program.cs`. Keep projects empty of business code.
- **Acceptance criteria:** Reference graph matches [architecture.md](../architecture/architecture.md#dependency-direction) and ADR 0001 (verified by inspection until FOUNDATION-006 automates it); API starts; `/openapi/v1.json` served in Development.
- **Dependencies:** none (governed by ADR 0001)
- **Affected area:** Api, Application, Infrastructure, solution file
- **Testing:** build; manual start of the API confirmed and reported.

### FOUNDATION-003 — PostgreSQL and EF Core
- **Status:** Done · **Priority:** P0 · **Agent:** Backend
- **Description:** Add `docker-compose.yml` with PostgreSQL for local development, EF Core + Npgsql in Infrastructure, an `AppDbContext` implementing an Application-level `IAppDbContext` (ADR 0001), conventions from [data-model.md](../architecture/data-model.md) (UUID keys, UTC timestamps, decimal precision), an initial empty migration, and a database health check.
- **Acceptance criteria:** `docker compose up` provides a database; migration applies to a clean DB; health check reports DB status; connection string comes from configuration/user-secrets, not source.
- **Dependencies:** FOUNDATION-001, FOUNDATION-002, FOUNDATION-004; ADR 0001
- **Affected area:** Infrastructure, Application (`IAppDbContext` port per ADR 0001), Api, repo root
- **Testing:** startup/health behavior tests without a database (unreachable database reports unhealthy; missing connection string fails at startup); migration applied manually to a real PostgreSQL from `docker compose` and reported in the PR. The automated migration-apply test needs the Testcontainers harness and is part of FOUNDATION-008 (which depends on this item). **Approval:** adding EF Core/Npgsql are already-approved stack dependencies; no further approval needed.

### FOUNDATION-004 — Application configuration
- **Status:** Done · **Priority:** P0 · **Agent:** Backend / Security
- **Description:** Typed options with startup validation, environment-specific settings, user-secrets for local secrets, documented configuration keys, and `.env`/secret handling rules.
- **Acceptance criteria:** Invalid or missing required config fails fast at startup with a clear message; no secrets in tracked files; configuration keys documented in `docs/engineering/development.md`.
- **Dependencies:** none
- **Affected area:** Api, Infrastructure
- **Testing:** host-startup tests (in `Soforr.Api.IntegrationTests`, `WebApplicationFactory`, no database) for: valid config starts, missing value fails naming the setting, missing section fails naming the section. The first real required settings arrive with FOUNDATION-003; this item delivers the validated-options mechanism, user-secrets wiring and the rules.

### FOUNDATION-005 — Logging, error handling and correlation IDs
- **Status:** Done · **Priority:** P0 · **Agent:** Backend
- **Description:** Serilog (console + structured), request logging, correlation ID middleware, centralized `ProblemDetails` exception handling, liveness/readiness health endpoints. Out of scope here (deferred to ORG-003, the first item with handlers and endpoints): the stable error `code`, field-level validation errors, and the `Result`-to-HTTP mapping.
- **Acceptance criteria:** Each request has a correlation ID in logs and response header; unhandled exceptions return generic RFC 9457 responses without internals; health endpoints work.
- **Dependencies:** FOUNDATION-004, FOUNDATION-006
- **Affected area:** Api
- **Testing:** `WebApplicationFactory` tests (no database needed): error response shape, correlation ID propagation, no stack trace leakage. **Approval:** Serilog is in the approved stack.

### FOUNDATION-006 — Testing infrastructure (unit, architecture, API skeleton)
- **Status:** Done · **Priority:** P0 · **Agent:** QA
- **Description:** Per [ADR 0005](../decisions/0005-testing-infrastructure.md): create `tests/Soforr.Domain.Tests`, `Soforr.Application.Tests`, `Soforr.Architecture.Tests` (plain reflection over assembly references) and a `Soforr.Api.IntegrationTests` skeleton using `WebApplicationFactory` without a database. CI then runs real tests. Split from the original combined item so it does not wait on PostgreSQL work.
- **Acceptance criteria:** `dotnet test` runs all projects and executes at least one test in the Application, Architecture and API projects (`Soforr.Domain.Tests` is created empty and receives its first tests with ORG-001, since Domain has no behavior yet; a placeholder test would be false coverage); architecture tests fail if Domain has any reference, or Application references Infrastructure/Api/Npgsql/ASP.NET Core; demonstrated once with a deliberate violation (then removed).
- **Dependencies:** FOUNDATION-002; ADR 0005
- **Affected area:** tests/, CI
- **Testing:** the tests themselves. Test-only packages: xUnit family, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing`.

### FOUNDATION-008 — PostgreSQL integration test harness
- **Status:** Backlog · **Priority:** P0 · **Agent:** QA / Backend
- **Description:** Testcontainers PostgreSQL fixture (one container per run, one database per test class created from migrations), `Integration` category, two-tenant fixture stub (completed in IDENTITY-003).
- **Acceptance criteria:** `dotnet test` needs only Docker; migrations apply to an empty database in a test; tests are order-independent; CI runs them.
- **Dependencies:** FOUNDATION-003, FOUNDATION-006; ADR 0005
- **Affected area:** tests/, CI
- **Testing:** the harness's own migration-apply test. Test-only package: `Testcontainers.PostgreSql`.

### FOUNDATION-007 — Web project scaffold
- **Status:** Ready · **Priority:** P1 · **Agent:** Frontend
- **Description:** Create `src/Soforr.Web` with Vue 3, TypeScript (strict), Vite, Tailwind, ESLint/Prettier, Vitest, router, and a typed API client approach aligned with the OpenAPI document. A placeholder shell page only; no business screens.
- **Acceptance criteria:** `npm ci && npm run build && npm test` pass; dev server proxies to the API; CI builds the frontend; no business rules in the client.
- **Dependencies:** none
- **Affected area:** src/Soforr.Web, CI
- **Testing:** Vitest smoke test; build in CI.

### SECURITY-001 — Repository security baseline
- **Status:** Ready · **Priority:** P2 · **Agent:** Security
- **Description:** Dependabot config, NuGet vulnerability check in CI, secret scanning guidance (and a pre-commit/CI secret scan if free tooling suffices), `SECURITY.md` describing private vulnerability reporting.
- **Acceptance criteria:** Vulnerable-package check fails CI on high severity; Dependabot enabled for NuGet, npm, GitHub Actions; `SECURITY.md` present.
- **Dependencies:** none
- **Affected area:** .github/, repo root
- **Testing:** verify the CI step runs and can fail (demonstrated once).

---

# EPIC 02 — Identity & Organizations

### ORG-001 — Organization domain model
- **Status:** Done · **Priority:** P0 · **Agent:** Backend / Architecture
- **Description:** `Organization` entity (name, currency, timezone), invariants, `Money`/currency value objects placed in Domain, `IClock` and id-generation abstractions. Establish the module folder layout and the handler pattern of [ADR 0003](../decisions/0003-commands-and-queries-without-mediator.md) (`ICommandHandler`/`IQueryHandler`, `Result<T>`/`Error`, registration scan) that later modules copy. No mediator dependency.
- **Acceptance criteria:** Domain has no external dependencies; invariants (non-empty name, valid ISO currency) enforced and tested; handler pattern in place and covered by tests with test-defined handlers (the Organization commands and queries themselves, and the `DomainException`-to-`Error` translation, arrive with ORG-003).
- **Dependencies:** FOUNDATION-002, FOUNDATION-006; ADR 0003
- **Affected area:** Domain, Application
- **Testing:** domain unit tests.

### ORG-002 — Organization persistence
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** EF Core configuration and migration for organizations; tenant-scoping infrastructure hooks (an `IOrganizationContext` abstraction in Application; filter wiring completed in IDENTITY-003).
- **Acceptance criteria:** Migration applies to clean DB; constraints (required name, currency length) in place.
- **Dependencies:** ORG-001, FOUNDATION-003, FOUNDATION-008
- **Affected area:** Infrastructure
- **Testing:** integration test for persistence round-trip and constraints.

### ORG-003 — Organization API
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** `CreateOrganization`, `UpdateOrganizationSettings`, `GetOrganization` commands/queries and thin endpoints, with validation and ProblemDetails errors. Authorization is added once IDENTITY-002 lands (until then endpoints are not exposed beyond Development).
- **Acceptance criteria:** OpenAPI documents the endpoints; handler `Result` errors (ADR-0003) are mapped centrally to RFC 9457 responses with a stable `code` extension, and validation errors list field-level problems (the generic error pipeline itself exists from FOUNDATION-005); endpoints are mapped only in Development until IDENTITY-002 is Done, proven by a test that they are absent in other environments.
- **Dependencies:** ORG-002, FOUNDATION-005
- **Affected area:** Application, Api
- **Testing:** handler unit tests; API integration tests.

### IDENTITY-001 — Authentication foundation
- **Status:** Backlog · **Priority:** P0 · **Agent:** Security / Backend
- **Description:** User registration/sign-in, credential storage, session/token model, membership of users in organizations, first user becomes Owner of a new organization. Implements [ADR 0002](../decisions/0002-authentication.md) (ASP.NET Core Identity, HttpOnly cookie session, one organization per user, antiforgery on unsafe methods, security-stamp revocation). Email verification and password reset are excluded (IDENTITY-004).
- **Acceptance criteria:** Users can register (creating User + Organization + Owner membership atomically), sign in and out over HTTPS; passwords hashed; lockout in place; `ICurrentUser` and `IOrganizationContext` expose user id and organization; no secrets in source or `localStorage`.
- **Dependencies:** ORG-003; ADR 0002 (Accepted)
- **Affected area:** Identity, Api, Infrastructure
- **Testing:** integration tests for register/sign-in/failure/lockout; unit tests for membership rules.

### IDENTITY-002 — Authorization foundation
- **Status:** Backlog · **Priority:** P0 · **Agent:** Security / Backend
- **Description:** Roles (Owner, Manager, Staff) and permissions; permission checks declared per use case in Application; default-deny policy at the API; apply to organization endpoints.
- **Acceptance criteria:** Unauthenticated → 401; authenticated without permission → 403; role/permission matrix documented; organization endpoints protected.
- **Dependencies:** IDENTITY-001
- **Affected area:** Identity, Application, Api
- **Testing:** authorization tests for every protected operation (matrix-driven).

### IDENTITY-003 — Tenant isolation
- **Status:** Backlog · **Priority:** P0 · **Agent:** Security / Backend
- **Description:** Organization context derived from the authenticated principal; EF global query filters; write-side ownership checks; a reusable test harness for cross-tenant tests; 404 for foreign resources. **Design changes to this mechanism require human approval.**
- **Acceptance criteria:** A test suite with two tenants proves that reads, writes and referenced-id injection across tenants fail for all existing tenant-owned entities; a new tenant-owned entity without a filter is caught by an architecture/convention test.
- **Dependencies:** IDENTITY-002, ORG-002
- **Affected area:** Infrastructure, Application, Api, tests
- **Testing:** cross-tenant integration tests (mandatory), convention test for `OrganizationId` on tenant-owned entities.

### AUDIT-001 — Audit events foundation
- **Status:** Backlog · **Priority:** P1 · **Agent:** Backend / Security
- **Description:** Append-only `AuditEvent` (who, what, when, organization, correlation ID, entity reference) and an Application-level `IAuditLog`; record authentication and role changes as first users.
- **Acceptance criteria:** No update/delete path via the application; events emitted for sign-in, role assignment; queryable by owner role.
- **Dependencies:** IDENTITY-003
- **Affected area:** Application, Infrastructure
- **Testing:** integration tests: event written in the same transaction as the command; tenant-scoped.

### IDENTITY-004 — Email verification and password reset
- **Status:** Backlog · **Priority:** P1 · **Agent:** Security / Backend
- **Description:** Added because ADR 0002 requires these before any external user and the backlog lacked them. Identity token providers for email confirmation and password reset; an `IEmailSender` port in Application with a development sender that only logs links. A real email provider is a paid external service and needs approval.
- **Acceptance criteria:** Tokens are single-purpose, short-lived, single-use; reset does not reveal whether an email exists; completing a reset invalidates existing sessions; unverified users are blocked from protected features once enforcement is enabled.
- **Dependencies:** IDENTITY-001, SECURITY-002
- **Affected area:** Identity, Application, Infrastructure
- **Testing:** integration tests for token expiry, reuse, enumeration resistance, session invalidation.

---

# EPIC 03 — Product Catalog

### CATALOG-001 — Product domain model
- **Status:** Done · **Priority:** P0 · **Agent:** Backend
- **Description:** `Product` (SKU, name, unit of measure, selling price, cost price, reorder point/lead time fields, active flag) with invariants from [modules.md](../architecture/modules.md#catalog).
- **Acceptance criteria:** Invariants enforced in Domain; no persistence or framework references.
- **Dependencies:** ORG-001 (shared `Money`/`Quantity` value objects)
- **Affected area:** Domain
- **Testing:** unit tests for invariants (price ≥ 0, SKU rules, deactivation).

### CATALOG-002 — Product persistence
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** EF configuration, migration, tenant filter, per-organization unique SKU compared case-insensitively with the entered casing preserved ([ADR 0006](../decisions/0006-sku-uniqueness.md)), indexes.
- **Acceptance criteria:** Migration applies cleanly; SKU uniqueness per organization is enforced by the database with a case-insensitive strategy (not only by handler checks); the stored SKU keeps its entered casing; no exception for deactivated products without a decision.
- **Dependencies:** CATALOG-001, IDENTITY-003
- **Affected area:** Infrastructure
- **Testing:** integration tests against real PostgreSQL, including cross-tenant isolation, SKU reuse across tenants, and a rejected duplicate that differs only by case (`ABC-123` vs `abc-123`) within one organization.

### CATALOG-003 — Product API
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** CreateProduct, UpdateProduct, DeactivateProduct, GetProduct, SearchProducts (paginated) with permissions.
- **Acceptance criteria:** Documented in OpenAPI; validation and error contract per conventions; permissions enforced; a duplicate SKU (case-insensitive, per ADR 0006) returns a conflict error and SKU lookups are case-insensitive.
- **Dependencies:** CATALOG-002, IDENTITY-002
- **Affected area:** Application, Api
- **Testing:** handler tests, API integration tests, authorization and tenant-isolation tests.

### CATALOG-004 — Product UI
- **Status:** Backlog · **Priority:** P1 · **Agent:** Frontend
- **Description:** Sign-in shell, product list/search, create/edit form. Client validation is UX only.
- **Acceptance criteria:** An owner can create, edit, search, deactivate products against the real API.
- **Dependencies:** CATALOG-003, FOUNDATION-007, IDENTITY-001
- **Affected area:** Web
- **Testing:** component tests; one end-to-end journey (sign in → create product).

---

# EPIC 04 — Inventory

### INVENTORY-001 — Inventory domain model and design note
- **Status:** In Review · **Priority:** P0 · **Agent:** Backend / Architecture
- **Description:** `InventoryMovement` (append-only), `StockLevel`, reasons, non-negative stock rule, `Quantity` value object. The concurrency mechanism is decided in [ADR 0004](../decisions/0004-inventory-concurrency.md) (optimistic token plus database backstops).
- **Acceptance criteria:** Domain model reflects ADR 0004 (append-only movements, reasons, idempotency source reference); invariants covered in tests.
- **Dependencies:** CATALOG-001; ADR 0004
- **Affected area:** Domain, docs
- **Testing:** unit tests for movement application and rule violations.

### INVENTORY-002 — Inventory persistence and movement recording
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** EF mapping, migration (CHECK constraints), internal `RecordMovement` service that updates `StockLevel` and writes the movement in one transaction following ADR 0004: concurrency token on `StockLevel`, command-level bounded retry with re-validation, `SaveChanges` interceptor rejecting movement updates/deletes, idempotency unique constraint, `CHECK (Quantity >= 0)`.
- **Acceptance criteria:** `StockLevel = SUM(movements)` holds after any sequence of operations; concurrent decrements never produce negative stock.
- **Dependencies:** INVENTORY-001, CATALOG-002, FOUNDATION-008
- **Affected area:** Infrastructure, Application
- **Testing:** integration tests with real PostgreSQL, including a concurrency test and a reconciliation test.

### INVENTORY-003 — Inventory API (adjustments and queries)
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** `AdjustInventory` (reason and note required, audited), `GetStockLevel`, `ListLowStock`, `GetMovementHistory`.
- **Acceptance criteria:** Every adjustment yields a movement and an audit event; permissions and tenant isolation enforced.
- **Dependencies:** INVENTORY-002, IDENTITY-002, AUDIT-001
- **Affected area:** Application, Api
- **Testing:** handler, API, authorization, tenant-isolation tests.

### INVENTORY-004 — Inventory UI
- **Status:** Backlog · **Priority:** P1 · **Agent:** Frontend
- **Description:** Stock levels view, movement history per product, manual adjustment form.
- **Acceptance criteria:** Owner can view stock and adjust with a reason.
- **Dependencies:** INVENTORY-003, CATALOG-004
- **Affected area:** Web
- **Testing:** component tests; end-to-end adjust-stock journey.

---

# EPIC 05 — Purchasing

### PURCHASING-001 — Purchasing domain and persistence
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** `Supplier`, `PurchaseOrder` with lines and state machine (draft → placed → partially received → received/cancelled); EF mapping and migration; totals from deterministic code.
- **Acceptance criteria:** Invalid state transitions rejected in Domain; totals correct; tenant-scoped.
- **Dependencies:** CATALOG-002, INVENTORY-001 (persistence portions: IDENTITY-003)
- **Affected area:** Domain, Infrastructure
- **Testing:** unit tests for state machine and totals; persistence integration tests.

### PURCHASING-002 — Purchase order and receiving commands
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** CreateSupplier, CreatePurchaseOrder, PlacePurchaseOrder, CancelPurchaseOrder, and **ReceivePurchase**, which writes `PurchaseReceived` inventory movements atomically and idempotently per receipt.
- **Acceptance criteria:** Receiving increases stock exactly once even if retried; partial receipt supported; failure leaves no partial state; audit event recorded.
- **Dependencies:** PURCHASING-001, INVENTORY-002, AUDIT-001
- **Affected area:** Application, Api
- **Testing:** atomicity, idempotency, partial receipt, authorization, tenant-isolation tests.

### PURCHASING-003 — Purchasing UI
- **Status:** Backlog · **Priority:** P1 · **Agent:** Frontend
- **Description:** Suppliers, purchase order create/place, receive goods.
- **Acceptance criteria:** Owner can buy and receive stock and see the stock level change.
- **Dependencies:** PURCHASING-002, INVENTORY-004
- **Affected area:** Web
- **Testing:** end-to-end journey (create product → purchase → receive → stock visible).

---

# EPIC 06 — Sales

### SALES-001 — Sales domain and persistence
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** `Sale`/`SaleLine` (walk-in; no customer required), server-computed totals, void semantics, EF mapping and migration.
- **Acceptance criteria:** Totals computed only by server code; completed sales immutable except void; tenant-scoped.
- **Dependencies:** CATALOG-002, INVENTORY-001 (persistence: IDENTITY-003)
- **Affected area:** Domain, Infrastructure
- **Testing:** unit tests for totals/rounding/void rules; persistence integration tests.

### SALES-002 — CreateSale and VoidSale commands
- **Status:** Backlog · **Priority:** P0 · **Agent:** Backend
- **Description:** `CreateSale` writes `SaleCompleted` movements in the same transaction; rejects insufficient stock; `VoidSale` restores stock via new movements; endpoints and permissions.
- **Acceptance criteria:** No partial state on failure; concurrent sales of the last unit result in one success; void restores stock exactly once; audited.
- **Dependencies:** SALES-001, INVENTORY-002, IDENTITY-002, AUDIT-001
- **Affected area:** Application, Api
- **Testing:** atomicity and concurrency integration tests; authorization and tenant-isolation tests.

### SALES-003 — Sales UI
- **Status:** Backlog · **Priority:** P1 · **Agent:** Frontend
- **Description:** Fast sale entry screen, sales list, void.
- **Acceptance criteria:** Owner can record and void a sale; stock reflects the change.
- **Dependencies:** SALES-002, INVENTORY-004
- **Affected area:** Web
- **Testing:** end-to-end journey completing the Product → Purchase → Sale flow.

---

# EPIC 09 — Business Intelligence (first slice tail) and EPIC 10 — Notifications

### INTEL-001 — Stock analysis and forecast
- **Status:** Backlog · **Priority:** P1 · **Agent:** Backend / AI
- **Description:** Deterministic sales-velocity forecast per product (projected days of stock remaining, projected stock-out date) with an explainable rationale. Method documented; no LLM.
- **Acceptance criteria:** Same inputs → same outputs; handles no history and zero velocity; results carry the data used to compute them.
- **Dependencies:** SALES-002, INVENTORY-003
- **Affected area:** Intelligence (Application/Domain)
- **Testing:** unit tests with fixed clock and fixtures; tenant isolation.

### INTEL-002 — Reorder recommendation generation
- **Status:** Backlog · **Priority:** P1 · **Agent:** Backend / AI
- **Description:** Generate `ReorderRecommendation` records (product, suggested quantity, rationale, status) from forecasts and product reorder settings; idempotent regeneration.
- **Acceptance criteria:** Recommendations appear for products projected to run out within lead time; no duplicate pending recommendations per product.
- **Dependencies:** INTEL-001, PURCHASING-001
- **Affected area:** Intelligence
- **Testing:** unit and integration tests for thresholds, duplicates, tenant isolation.

### INTEL-003 — Owner approval executes purchase order
- **Status:** Backlog · **Priority:** P1 · **Agent:** Backend / Frontend
- **Description:** `ApproveReorder` (Purchasing command) invoked when the owner approves a recommendation; creates a draft/placed purchase order, marks the recommendation approved with actor, audited. `DismissRecommendation`. Simple UI list of recommendations with approve/dismiss.
- **Acceptance criteria:** Approval is executed by the application (not any AI), only by an authorized user, exactly once; the audit event links user, recommendation and purchase order.
- **Dependencies:** INTEL-002, PURCHASING-002
- **Affected area:** Purchasing, Intelligence, Web
- **Testing:** approval idempotency, permissions, audit, tenant isolation; end-to-end journey completing the whole first vertical slice.

### NOTIF-001 — In-app notifications for recommendations
- **Status:** Backlog · **Priority:** P2 · **Agent:** Backend / Frontend
- **Description:** In-app notification when new reorder recommendations exist; read state.
- **Acceptance criteria:** Owner sees unread notifications referencing verified recommendations.
- **Dependencies:** INTEL-002
- **Affected area:** Notifications, Web
- **Testing:** unit/integration tests; tenant and recipient scoping.

---

# Cross-Cutting

### SECURITY-002 — Rate limiting and API hardening
- **Status:** Backlog · **Priority:** P1 · **Agent:** Security
- **Description:** ASP.NET Core rate limiting (strict on authentication endpoints), request size limits, security headers, CORS allowlist, HSTS.
- **Acceptance criteria:** Limits configurable; exceeded limits return 429; headers verified. Required before any external user.
- **Dependencies:** IDENTITY-001
- **Affected area:** Api
- **Testing:** integration tests for 429 and headers.

### DEPLOY-001 — Local container development
- **Status:** Backlog · **Priority:** P2 · **Agent:** Backend
- **Description:** Dockerfile for the API and compose profile running API + PostgreSQL locally. No production deployment.
- **Acceptance criteria:** `docker compose up` starts a working local system.
- **Dependencies:** FOUNDATION-003
- **Affected area:** repo root
- **Testing:** start and hit health endpoint. **Production deployment is out of scope and requires approval.**

---

# Later Epics (not yet decomposed)

Decompose into items when their dependencies approach Done. Do not start these before the first vertical slice is complete.

| Epic | Depends on | Notes |
|---|---|---|
| EPIC 07 — Customers | SALES-002 | Attach optional customer to sales |
| EPIC 08 — Expenses | IDENTITY-003 | Simple expense recording; receipts via Files |
| EPIC 13 — Files & Receipts | IDENTITY-003, SECURITY-002 | Upload security per [security.md](../security/security.md#file-uploads) |
| EPIC 14 — Reporting | SALES-002, PURCHASING-002 | Read-only, deterministic |
| EPIC 11 — Automation | INTEL-003, NOTIF-001 | Only pre-approved safe commands |
| EPIC 12 — AI Assistant | INTEL-003 | Read-only tools, provider abstraction; **paid provider needs approval** |
| EPIC 15 — SaaS Billing | first product proven | **Needs approval** (paid services) |
| EPIC 16 — Security & Compliance | progressive | Baseline items above; review before external users |
| EPIC 17 — Observability | FOUNDATION-005 | Metrics/tracing, automation history |
| EPIC 18 — Deployment & Infrastructure | DEPLOY-001 | **Production deployment requires approval** |

# Immediately Executable (dependencies satisfied)

FOUNDATION-001, FOUNDATION-002, FOUNDATION-004, FOUNDATION-007, SECURITY-001.
