# Master Backlog

Agents work from this backlog. Take the **highest-priority `Ready` item whose dependencies are all `Done`**, in the order given by [Sequence to Sellable V1](#sequence-to-sellable-v1). Lifecycle and approval rules: [development.md](../engineering/development.md). Completion criteria: [definition-of-done.md](../engineering/definition-of-done.md). Phases: [roadmap.md](roadmap.md). What "sellable" means: [sellable-v1.md](sellable-v1.md).

**Status values:** `Backlog` (not yet refined or dependencies open) · `Ready` (refined, dependencies Done) · `In Progress` · `In Review` · `Done`.

**Priority classes** (the sequencing constraint is sellability, see [sellable-v1.md](sellable-v1.md#sequencing-rule)):

| Class | Meaning |
|---|---|
| **SELLABLE-P0** | Required to make the first customer-ready product. Always ahead of everything else. |
| **PRODUCT-P1** | Valuable product capability that is not required for the first sale. Not started while a SELLABLE-P0 item is available. |
| **FUTURE** | Long-term capability (Horizon 2 and beyond). Not started before V1 is sold and observed. |

Deterministic intelligence (EPIC 09) is **SELLABLE-P0**; statistical and AI intelligence are FUTURE. Cut scope, not quality: no item's testing, security, tenant-isolation or integrity requirements are reduced by its class.

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
| 0004 Inventory concurrency | INVENTORY-001, INVENTORY-002, INVENTORY-003 and every later stock-changing item |
| 0006 SKU uniqueness | CATALOG-002, CATALOG-003 |

Not governed by an ADR: FOUNDATION-001, FOUNDATION-004, FOUNDATION-007, SECURITY-001. The intelligence items (EPIC 09) are governed by [architecture.md](../architecture/architecture.md#deterministic-intelligence); INTEL-001 records an ADR if it makes a real architectural choice (computed or materialized insights).

## Dependency Graph

The core of Sellable V1 is one vertical slice; everything else supports it or follows it.

```text
Engineering Foundation (EPIC 01)
        ↓
Organizations, Authentication, Authorization, Tenant isolation, Audit
        ↓
Product Catalog (CATALOG)
        ↓
Inventory (INVENTORY)
        ↓
Purchasing (PURCHASING)  ──┐
        ↓                  │ (both need Inventory, not each other)
Sales (SALES)  ←───────────┘
        ↓
Customers and balances (CUSTOMER)
        ↓
Deterministic intelligence (INTEL-001..007)
        ↓
First complete journey (E2E-001) → Demonstration → Production readiness → Pilot
        ↓
PRODUCT-P1 → FUTURE (statistical, AI-assisted, automation)
```

## Sequence to Sellable V1

Complete one strong vertical slice before widening. Tracks may run in parallel where the dependencies allow.

| Step | Items |
|---|---|
| 1. Unblock the slice | AUDIT-001, SECURITY-001, SECURITY-002, FOUNDATION-007 |
| 2. Inventory API | INVENTORY-003 |
| 3. Purchasing | PURCHASING-001, PURCHASING-002 |
| 4. Sales | SALES-001, SALES-002 |
| 5. First intelligence | INTEL-001, INTEL-002, INTEL-003 |
| 6. Customers and the rest of the intelligence | CUSTOMER-001, CUSTOMER-002, INTEL-004, INTEL-005, INTEL-006 |
| 7. Screens | CATALOG-004, INVENTORY-004, PURCHASING-003, SALES-003, CUSTOMER-003, INTEL-007 |
| 8. People and hardening | IDENTITY-004, IDENTITY-005 |
| 9. Production | DEPLOY-001, DEPLOY-002, OBS-001, OPS-001 |
| 10. Commercial | E2E-001, COMMERCIAL-001, COMMERCIAL-002 |

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
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Architecture / Backend
- **Description:** Establish repo-wide build conventions: `global.json` pinning the .NET 10 SDK with `rollForward: latestFeature` (developer machines may also have .NET 9 installed; without a pin the SDK in use is implicit), `Directory.Build.props` (nullable, warnings, analyzers, `TreatWarningsAsErrors` for CI), `.editorconfig`, `.gitattributes`, and `Directory.Packages.props` for central package versions. Update `.github/workflows/ci.yml` to use `global-json-file` in `setup-dotnet` and add a `dotnet format --verify-no-changes` step.
- **Acceptance criteria:** `dotnet build` and `dotnet format --verify-no-changes` pass at the repo root; CI uses the same SDK as `global.json`; package versions live in one place; no behavior changes.
- **Dependencies:** none
- **Affected area:** repository root, all projects
- **Testing:** build + format verification; CI green (until FOUNDATION-006 the CI test step passes vacuously because there are no test projects).

### FOUNDATION-002 — Solution structure and composition roots
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** The four backend projects already exist with the agreed references. Add per-layer registration entry points (`AddApplication()`, `AddInfrastructure(config)`), add a `tests/` solution folder, and wire the composition in `Program.cs`. Keep projects empty of business code.
- **Acceptance criteria:** Reference graph matches [architecture.md](../architecture/architecture.md#dependency-direction) and ADR 0001 (verified by inspection until FOUNDATION-006 automates it); API starts; `/openapi/v1.json` served in Development.
- **Dependencies:** none (governed by ADR 0001)
- **Affected area:** Api, Application, Infrastructure, solution file
- **Testing:** build; manual start of the API confirmed and reported.

### FOUNDATION-003 — PostgreSQL and EF Core
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** Add `docker-compose.yml` with PostgreSQL for local development, EF Core + Npgsql in Infrastructure, an `AppDbContext` implementing an Application-level `IAppDbContext` (ADR 0001), conventions from [data-model.md](../architecture/data-model.md) (UUID keys, UTC timestamps, decimal precision), an initial empty migration, and a database health check.
- **Acceptance criteria:** `docker compose up` provides a database; migration applies to a clean DB; health check reports DB status; connection string comes from configuration/user-secrets, not source.
- **Dependencies:** FOUNDATION-001, FOUNDATION-002, FOUNDATION-004; ADR 0001
- **Affected area:** Infrastructure, Application (`IAppDbContext` port per ADR 0001), Api, repo root
- **Testing:** startup/health behavior tests without a database (unreachable database reports unhealthy; missing connection string fails at startup); migration applied manually to a real PostgreSQL from `docker compose` and reported in the PR. The automated migration-apply test needs the Testcontainers harness and is part of FOUNDATION-008 (which depends on this item). **Approval:** adding EF Core/Npgsql are already-approved stack dependencies; no further approval needed.

### FOUNDATION-004 — Application configuration
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend / Security
- **Description:** Typed options with startup validation, environment-specific settings, user-secrets for local secrets, documented configuration keys, and `.env`/secret handling rules.
- **Acceptance criteria:** Invalid or missing required config fails fast at startup with a clear message; no secrets in tracked files; configuration keys documented in `docs/engineering/development.md`.
- **Dependencies:** none
- **Affected area:** Api, Infrastructure
- **Testing:** host-startup tests (in `Soforr.Api.IntegrationTests`, `WebApplicationFactory`, no database) for: valid config starts, missing value fails naming the setting, missing section fails naming the section. The first real required settings arrive with FOUNDATION-003; this item delivers the validated-options mechanism, user-secrets wiring and the rules.

### FOUNDATION-005 — Logging, error handling and correlation IDs
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** Serilog (console + structured), request logging, correlation ID middleware, centralized `ProblemDetails` exception handling, liveness/readiness health endpoints. Out of scope here (deferred to ORG-003, the first item with handlers and endpoints): the stable error `code`, field-level validation errors, and the `Result`-to-HTTP mapping.
- **Acceptance criteria:** Each request has a correlation ID in logs and response header; unhandled exceptions return generic RFC 9457 responses without internals; health endpoints work.
- **Dependencies:** FOUNDATION-004, FOUNDATION-006
- **Affected area:** Api
- **Testing:** `WebApplicationFactory` tests (no database needed): error response shape, correlation ID propagation, no stack trace leakage. **Approval:** Serilog is in the approved stack.

### FOUNDATION-006 — Testing infrastructure (unit, architecture, API skeleton)
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** QA
- **Description:** Per [ADR 0005](../decisions/0005-testing-infrastructure.md): create `tests/Soforr.Domain.Tests`, `Soforr.Application.Tests`, `Soforr.Architecture.Tests` (plain reflection over assembly references) and a `Soforr.Api.IntegrationTests` skeleton using `WebApplicationFactory` without a database. CI then runs real tests. Split from the original combined item so it does not wait on PostgreSQL work.
- **Acceptance criteria:** `dotnet test` runs all projects and executes at least one test in the Application, Architecture and API projects (`Soforr.Domain.Tests` is created empty and receives its first tests with ORG-001, since Domain has no behavior yet; a placeholder test would be false coverage); architecture tests fail if Domain has any reference, or Application references Infrastructure/Api/Npgsql/ASP.NET Core; demonstrated once with a deliberate violation (then removed).
- **Dependencies:** FOUNDATION-002; ADR 0005
- **Affected area:** tests/, CI
- **Testing:** the tests themselves. Test-only packages: xUnit family, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing`.

### FOUNDATION-008 — PostgreSQL integration test harness
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** QA / Backend
- **Description:** Testcontainers PostgreSQL fixture (one container per run, one database per test class created from migrations), `Integration` category, two-tenant fixture stub (completed in IDENTITY-003).
- **Acceptance criteria:** `dotnet test` needs only Docker; migrations apply to an empty database in a test; tests are order-independent; CI runs them.
- **Dependencies:** FOUNDATION-003, FOUNDATION-006; ADR 0005
- **Affected area:** tests/, CI
- **Testing:** the harness's own migration-apply test. Test-only package: `Testcontainers.PostgreSql`.

### FOUNDATION-007 — Web project scaffold
- **Status:** Ready · **Priority:** SELLABLE-P0 · **Agent:** Frontend
- **Description:** Create `src/Soforr.Web` with Vue 3, TypeScript (strict), Vite, Tailwind, ESLint/Prettier, Vitest, router, and a typed API client approach aligned with the OpenAPI document. A placeholder shell page only; no business screens.
- **Acceptance criteria:** `npm ci && npm run build && npm test` pass; dev server proxies to the API; CI builds the frontend; no business rules in the client.
- **Dependencies:** none
- **Affected area:** src/Soforr.Web, CI
- **Testing:** Vitest smoke test; build in CI.

### SECURITY-001 — Repository security baseline
- **Status:** Ready · **Priority:** SELLABLE-P0 · **Agent:** Security
- **Description:** Dependabot config, NuGet vulnerability check in CI, secret scanning guidance (and a pre-commit/CI secret scan if free tooling suffices), `SECURITY.md` describing private vulnerability reporting.
- **Acceptance criteria:** Vulnerable-package check fails CI on high severity; Dependabot enabled for NuGet, npm, GitHub Actions; `SECURITY.md` present.
- **Dependencies:** none
- **Affected area:** .github/, repo root
- **Testing:** verify the CI step runs and can fail (demonstrated once).

---

# EPIC 02 — Identity & Organizations

### ORG-001 — Organization domain model
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend / Architecture
- **Description:** `Organization` entity (name, currency, timezone), invariants, `Money`/currency value objects placed in Domain, `IClock` and id-generation abstractions. Establish the module folder layout and the handler pattern of [ADR 0003](../decisions/0003-commands-and-queries-without-mediator.md) (`ICommandHandler`/`IQueryHandler`, `Result<T>`/`Error`, registration scan) that later modules copy. No mediator dependency.
- **Acceptance criteria:** Domain has no external dependencies; invariants (non-empty name, valid ISO currency) enforced and tested; handler pattern in place and covered by tests with test-defined handlers (the Organization commands and queries themselves, and the `DomainException`-to-`Error` translation, arrive with ORG-003).
- **Dependencies:** FOUNDATION-002, FOUNDATION-006; ADR 0003
- **Affected area:** Domain, Application
- **Testing:** domain unit tests.

### ORG-002 — Organization persistence
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** EF Core configuration and migration for organizations; tenant-scoping infrastructure hooks (an `IOrganizationContext` abstraction in Application; filter wiring completed in IDENTITY-003).
- **Acceptance criteria:** Migration applies to clean DB; constraints (required name, currency length) in place.
- **Dependencies:** ORG-001, FOUNDATION-003, FOUNDATION-008
- **Affected area:** Infrastructure
- **Testing:** integration test for persistence round-trip and constraints.

### ORG-003 — Organization API
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** `CreateOrganization`, `UpdateOrganizationSettings`, `GetOrganization` commands/queries and thin endpoints, with validation and ProblemDetails errors. Authorization is added once IDENTITY-002 lands (until then endpoints are not exposed beyond Development).
- **Acceptance criteria:** OpenAPI documents the endpoints; handler `Result` errors (ADR-0003) are mapped centrally to RFC 9457 responses with a stable `code` extension, and validation errors list field-level problems (the generic error pipeline itself exists from FOUNDATION-005); endpoints are mapped only in Development until IDENTITY-002 is Done, proven by a test that they are absent in other environments.
- **Dependencies:** ORG-002, FOUNDATION-005
- **Affected area:** Application, Api
- **Testing:** handler unit tests; API integration tests.

### IDENTITY-001 — Authentication foundation
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Security / Backend
- **Description:** User registration/sign-in, credential storage, session/token model, membership of users in organizations, first user becomes Owner of a new organization. Implements [ADR 0002](../decisions/0002-authentication.md) (ASP.NET Core Identity, HttpOnly cookie session, one organization per user, antiforgery on unsafe methods, security-stamp revocation). Email verification and password reset are excluded (IDENTITY-004).
- **Acceptance criteria:** Users can register (creating User + Organization + Owner membership atomically), sign in and out over HTTPS; passwords hashed; lockout in place; `ICurrentUser` and `IOrganizationContext` expose user id and organization; no secrets in source or `localStorage`.
- **Dependencies:** ORG-003; ADR 0002 (Accepted)
- **Affected area:** Identity, Api, Infrastructure
- **Testing:** integration tests for register/sign-in/failure/lockout; unit tests for membership rules.

### IDENTITY-002 — Authorization foundation
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Security / Backend
- **Description:** Roles (Owner, Manager, Staff) and permissions; permission checks declared per use case in Application; default-deny policy at the API; apply to organization endpoints.
- **Acceptance criteria:** Unauthenticated → 401; authenticated without permission → 403; role/permission matrix documented; organization endpoints protected.
- **Dependencies:** IDENTITY-001
- **Affected area:** Identity, Application, Api
- **Testing:** authorization tests for every protected operation (matrix-driven).

### IDENTITY-003 — Tenant isolation
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Security / Backend
- **Description:** Organization context derived from the authenticated principal; EF global query filters; write-side ownership checks; a reusable test harness for cross-tenant tests; 404 for foreign resources. **Design changes to this mechanism require human approval.**
- **Acceptance criteria:** A test suite with two tenants proves that reads, writes and referenced-id injection across tenants fail for all existing tenant-owned entities; a new tenant-owned entity without a filter is caught by an architecture/convention test.
- **Dependencies:** IDENTITY-002, ORG-002
- **Affected area:** Infrastructure, Application, Api, tests
- **Testing:** cross-tenant integration tests (mandatory), convention test for `OrganizationId` on tenant-owned entities.

### AUDIT-001 — Audit events foundation
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend / Security
- **Description:** Append-only `AuditEvent` (who, what, when, organization, correlation ID, entity reference) and an Application-level `IAuditLog`; record authentication and role changes as first users.
- **Acceptance criteria:** No update/delete path via the application; events emitted for sign-in, role assignment; queryable by owner role.
- **Dependencies:** IDENTITY-003
- **Affected area:** Application, Infrastructure
- **Testing:** integration tests: event written in the same transaction as the command; tenant-scoped.

### IDENTITY-004 — Email verification and password reset
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Security / Backend
- **Description:** Added because ADR 0002 requires these before any external user and the backlog lacked them. Identity token providers for email confirmation and password reset; an `IEmailSender` port in Application with a development sender that only logs links. A real email provider is a paid external service and needs approval.
- **Acceptance criteria:** Tokens are single-purpose, short-lived, single-use; reset does not reveal whether an email exists; completing a reset invalidates existing sessions; unverified users are blocked from protected features once enforcement is enabled.
- **Dependencies:** IDENTITY-001, SECURITY-002
- **Affected area:** Identity, Application, Infrastructure
- **Testing:** integration tests for token expiry, reuse, enumeration resistance, session invalidation.

### IDENTITY-005 — Inviting and managing an organization's users
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Security / Backend
- **Description:** Added because roles exist but nothing lets an Owner give a second person access. The Owner invites a user by e-mail with a role (Manager or Staff), the user accepts through a single-use, short-lived token and sets a password, the Owner can change a role or deactivate a user. New permission `MembersManage` (Owner only). The organization always keeps at least one Owner ([ADR 0002](../decisions/0002-authentication.md)); a role change or deactivation updates the security stamp so existing sessions end; audited. Uses the `IEmailSender` port of IDENTITY-004.
- **Acceptance criteria:** Only an Owner can invite or change members; an invited user joins only the inviting organization; an expired or reused invitation fails; the last Owner cannot be removed or demoted; changes take effect for existing sessions within the validation interval; every action is audited.
- **Dependencies:** IDENTITY-004, AUDIT-001
- **Affected area:** Identity, Application, Api, Web
- **Testing:** authorization matrix, token expiry/reuse, last-Owner invariant, tenant isolation, session revocation.

---

# EPIC 03 — Product Catalog

### CATALOG-001 — Product domain model
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** `Product` (SKU, name, unit of measure, selling price, cost price, reorder point/lead time fields, active flag) with invariants from [modules.md](../architecture/modules.md#catalog).
- **Acceptance criteria:** Invariants enforced in Domain; no persistence or framework references.
- **Dependencies:** ORG-001 (shared `Money`/`Quantity` value objects)
- **Affected area:** Domain
- **Testing:** unit tests for invariants (price ≥ 0, SKU rules, deactivation).

### CATALOG-002 — Product persistence
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** EF configuration, migration, tenant filter, per-organization unique SKU compared case-insensitively with the entered casing preserved ([ADR 0006](../decisions/0006-sku-uniqueness.md)), indexes.
- **Acceptance criteria:** Migration applies cleanly; SKU uniqueness per organization is enforced by the database with a case-insensitive strategy (not only by handler checks); the stored SKU keeps its entered casing; no exception for deactivated products without a decision.
- **Dependencies:** CATALOG-001, IDENTITY-003
- **Affected area:** Infrastructure
- **Testing:** integration tests against real PostgreSQL, including cross-tenant isolation, SKU reuse across tenants, and a rejected duplicate that differs only by case (`ABC-123` vs `abc-123`) within one organization.

### CATALOG-003 — Product API
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** CreateProduct, UpdateProduct, DeactivateProduct, GetProduct, SearchProducts (paginated) with permissions.
- **Acceptance criteria:** Documented in OpenAPI; validation and error contract per conventions; permissions enforced; a duplicate SKU (case-insensitive, per ADR 0006) returns a conflict error and SKU lookups are case-insensitive.
- **Dependencies:** CATALOG-002, IDENTITY-002
- **Affected area:** Application, Api
- **Testing:** handler tests, API integration tests, authorization and tenant-isolation tests.

### CATALOG-004 — Product UI
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Frontend
- **Description:** Sign-in shell, product list/search, create/edit form. Client validation is UX only.
- **Acceptance criteria:** An owner can create, edit, search, deactivate products against the real API.
- **Dependencies:** CATALOG-003, FOUNDATION-007, IDENTITY-001
- **Affected area:** Web
- **Testing:** component tests; one end-to-end journey (sign in → create product).

---

# EPIC 04 — Inventory

### CATALOG-005 — Product barcode and fast lookup
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Backend / Frontend
- **Description:** Add one optional primary barcode/GTIN to Product, unique within an organization, with API lookup and fast sales/purchasing lookup. USB barcode scanners that behave as keyboard input must work with the web sale screen; camera scanning is a UI enhancement, not a Domain requirement.
- **Acceptance criteria:** blank/invalid barcodes rejected; duplicate barcode within an organization conflicts; the same barcode may exist in another organization; lookup is tenant-scoped; sale entry can add a product by barcode without manual SKU search; barcode changes are audited.
- **Dependencies:** CATALOG-002, CATALOG-003, CATALOG-004, AUDIT-001
- **Affected area:** Domain, Application, Infrastructure, Api, Web
- **Testing:** unit, persistence, API, authorization, tenant-isolation and end-to-end scan/lookup tests.


### INVENTORY-001 — Inventory domain model and design note
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend / Architecture
- **Description:** `InventoryMovement` (append-only), `StockLevel`, reasons, non-negative stock rule, `Quantity` value object. The concurrency mechanism is decided in [ADR 0004](../decisions/0004-inventory-concurrency.md) (optimistic token plus database backstops).
- **Acceptance criteria:** Domain model reflects ADR 0004 (append-only movements, reasons, idempotency source reference); invariants covered in tests.
- **Dependencies:** CATALOG-001; ADR 0004
- **Affected area:** Domain, docs
- **Testing:** unit tests for movement application and rule violations.

### INVENTORY-002 — Inventory persistence and movement recording
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** EF mapping, migration (CHECK constraints), internal `StockMovementRecorder` that updates `StockLevel` and writes the movement in one transaction following ADR 0004: `xmin` concurrency token on `StockLevel`, command-level bounded retry with re-validation (stock conflicts only), append-only movements enforced by the context, idempotency unique constraint, `CHECK (Quantity >= 0)`. Implemented as described in the ADR's implementation notes.
- **Acceptance criteria:** `StockLevel = SUM(movements)` holds after any sequence of operations; concurrent decrements never produce negative stock.
- **Dependencies:** INVENTORY-001, CATALOG-002, FOUNDATION-008
- **Affected area:** Infrastructure, Application
- **Testing:** integration tests with real PostgreSQL, including a concurrency test and a reconciliation test.

### INVENTORY-003 — Inventory API (adjustments and queries)
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** `AdjustInventory` (reason and note required, audited), `GetStockLevel`, `ListLowStock`, `GetMovementHistory`. Low stock means an active product with reorder settings whose stock is at or below its reorder point (defined once, shared by the stock level and the list); it is the data behind INTEL-002.
- **Acceptance criteria:** Every adjustment yields a movement and an audit event; permissions and tenant isolation enforced.
- **Dependencies:** INVENTORY-002, IDENTITY-002, AUDIT-001
- **Affected area:** Application, Api
- **Testing:** handler, API, authorization, tenant-isolation tests.

### INVENTORY-004 — Inventory UI
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Frontend
- **Description:** Stock levels view, movement history per product, manual adjustment form.
- **Acceptance criteria:** Owner can view stock and adjust with a reason.
- **Dependencies:** INVENTORY-003, CATALOG-004
- **Affected area:** Web
- **Testing:** component tests; end-to-end adjust-stock journey.

---

# EPIC 05 — Purchasing

### PURCHASING-001 — Purchasing domain and persistence
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** `Supplier`, `PurchaseOrder` with lines and state machine (draft → placed → partially received → received/cancelled); EF mapping and migration; totals from deterministic code. **Each line records the product's name and SKU and the unit cost at the time** (a snapshot, not a reference to the product's current values), because purchasing cost intelligence ([INTEL-006](#intel-006--purchasing-cost-intelligence)) and margins read them.
- **Acceptance criteria:** Invalid state transitions rejected in Domain; totals correct; tenant-scoped.
- **Dependencies:** CATALOG-002, INVENTORY-001 (persistence portions: IDENTITY-003)
- **Affected area:** Domain, Infrastructure
- **Testing:** unit tests for state machine and totals; persistence integration tests.

### PURCHASING-002 — Purchase order and receiving commands
- **Status:** Done · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** CreateSupplier, CreatePurchaseOrder, PlacePurchaseOrder, CancelPurchaseOrder, and **ReceivePurchase**, which writes `PurchaseReceived` inventory movements atomically and idempotently per receipt.
- **Acceptance criteria:** Receiving increases stock exactly once even if retried; partial receipt supported; failure leaves no partial state; audit event recorded. `CreatePurchaseOrder` takes no currency from the client: the handler obtains the organization's currency from `OrganizationCurrency.ResolveAsync` and passes it to the Domain (tests: the order is in the organization's currency; an unresolvable organization creates nothing; a currency in the request body is not honored; an API round trip reads back that currency).
- **Dependencies:** PURCHASING-001, INVENTORY-002, AUDIT-001
- **Affected area:** Application, Api
- **Testing:** atomicity, idempotency, partial receipt, authorization, tenant-isolation tests.

### PURCHASING-003 — Purchasing UI
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Frontend
- **Description:** Suppliers, purchase order create/place, receive goods.
- **Acceptance criteria:** Owner can buy and receive stock and see the stock level change.
- **Dependencies:** PURCHASING-002, INVENTORY-004
- **Affected area:** Web
- **Testing:** end-to-end journey (create product → purchase → receive → stock visible).

---

# EPIC 06 — Sales

### SALES-001 — Sales domain and persistence
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** `Sale`/`SaleLine` (walk-in; no customer required), server-computed totals, controlled line-price override/discount fields, void semantics, EF mapping and migration. **Each line records the product's name and SKU, the catalog unit price, the final applied unit price, the discount/override reason when applicable, the cost price and the currency at the time of the sale** (snapshots), so sales and margin intelligence never read the product's current values and history does not change when a product is edited.
- **Price rule:** product selling price is the default. A different line price or discount is allowed only through an explicit permission and server-side rule; the final price can never be negative and every override is attributable. V1 does not implement complex price lists or volume tiers.
- **Acceptance criteria:** Totals computed only by server code; completed sales immutable except void; tenant-scoped; line snapshots are stored and never updated.
- **Dependencies:** CATALOG-002, INVENTORY-001 (persistence: IDENTITY-003)
- **Affected area:** Domain, Infrastructure
- **Testing:** unit tests for totals/rounding/void/price-override rules; persistence integration tests.

### SALES-002 — CreateSale and VoidSale commands
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** `CreateSale` writes `SaleCompleted` movements in the same transaction; rejects insufficient stock; `VoidSale` restores stock via new movements; endpoints and permissions.
- **Acceptance criteria:** No partial state on failure; concurrent sales of the last unit result in one success; void restores stock exactly once; audited.
- **Dependencies:** SALES-001, INVENTORY-002, IDENTITY-002, AUDIT-001
- **Affected area:** Application, Api
- **Testing:** atomicity and concurrency integration tests; authorization and tenant-isolation tests.

### SALES-004 — Controlled price overrides and discounts
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** Enforce the V1 sale pricing rule: catalog price is the default; authorized users may apply a line-price override or discount within explicit limits; the original price, final price, reason and actor are preserved.
- **Acceptance criteria:** unauthorized users cannot override price; every override is attributable; server calculates totals; no price can fall below zero; margin intelligence uses the final sale-line price; audit event is written for sensitive overrides.
- **Dependencies:** SALES-002, IDENTITY-002, AUDIT-001
- **Affected area:** Application, Api
- **Testing:** authorization matrix, validation, audit, tenant isolation, totals and margin snapshot tests.


### SALES-003 — Sales UI
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Frontend
- **Description:** Fast sale entry screen, sales list, void.
- **Acceptance criteria:** Owner can record and void a sale; stock reflects the change.
- **Dependencies:** SALES-002, INVENTORY-004
- **Affected area:** Web
- **Testing:** end-to-end journey completing the Product → Purchase → Sale flow.

---

# EPIC 07 — Customers and Balances (Sellable V1, proposed scope)

Needed for customer balance and aging intelligence. Customers, credit sales and recorded payments are an accepted V1 product decision; see [sellable-v1.md](sellable-v1.md#accepted-v1-product-decisions).

### CUSTOMER-001 — Customers (domain, persistence, API)
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** `Customer` (name, contact details, optional credit terms such as days until a balance is overdue), tenant-owned, never deleted (deactivated). Create, update, deactivate, get, search (paginated). Permissions in the matrix.
- **Acceptance criteria:** Invariants in Domain; tenant filter and write guard; OpenAPI; permissions enforced; authorization and tenant-isolation tests.
- **Dependencies:** IDENTITY-003
- **Affected area:** Domain, Application, Infrastructure, Api
- **Testing:** unit, handler, API, persistence, authorization, tenant-isolation tests.

### CUSTOMER-002 — Credit sales and recorded payments
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** A sale can reference a customer and be paid in full, in part or not at all; payments are recorded against a sale (amount, date, method label), never processed. Outstanding balance per sale and per customer is derived from sales and payments (deterministic, never edited). Payment recording is append-only with a void/reversal by new record.
- **Acceptance criteria:** Balance = sum of sale totals − sum of payments, reproducible from records; over-payment rejected or recorded as a credit by an explicit documented rule; no money processing; audited.
- **Dependencies:** SALES-002, CUSTOMER-001, AUDIT-001
- **Decisions (product owner):** over-payment is rejected; only sales with a customer can be unpaid or part-paid (a walk-in sale is paid in full); a customer without credit terms is due immediately; `CreateSale` takes an optional amount paid and records the payment in the same transaction.
- **Progress:** part 1 (sale on account, fixed due date, append-only payment records, derived balance in the Domain, `CreateSale` with customer and amount paid, void refused while money is on the sale) is Done; part 2 (`RecordPayment`, `ReversePayment`, `GetCustomerBalance`, their endpoints and permissions) is In Review.
- **Affected area:** Domain, Application, Infrastructure, Api
- **Testing:** unit tests for the balance; concurrency and idempotency of payment recording; authorization; tenant isolation.

### CUSTOMER-003 — Customers UI
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Frontend
- **Description:** Customer list and form, attach a customer to a sale, record a payment, see a customer's balance.
- **Acceptance criteria:** An owner can sell on credit, record a payment and see the balance change.
- **Dependencies:** CUSTOMER-002, FOUNDATION-007
- **Affected area:** Web
- **Testing:** component tests; end-to-end journey.

---

# EPIC 09 — Deterministic Operational Intelligence (Sellable V1)

Layer 1 intelligence ([sellable-v1.md](sellable-v1.md#intelligence-layers)): explicit rules and documented formulas over authoritative data, no model. These items replace the earlier INTEL-001..003 and NOTIF-001 sequence; see the mapping at the end of this section.

**Rules for every intelligence item**
- The formula, the comparison period and the thresholds are written down in the item's documentation and covered by tests with a fixed clock; same data and clock give the same result.
- Every insight carries its facts (the named numbers it was built from) and a plain reason; nothing is shown that cannot be traced to records.
- Insights are computed from authoritative data and are never the record of what happened; no stored insight overrides a transaction.
- Tenant-scoped through the existing filters; permission-checked; cost-derived insights (margin, purchase cost) require `ProductViewCost`.
- No LLM, no forecasting infrastructure, no agent. Smallest structure that works: a module inside `Soforr.Application` (see [architecture.md](../architecture/architecture.md#deterministic-intelligence)).

### INTEL-001 — Intelligence foundation
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend / Architecture
- **Description:** The smallest shared structure for insights: an `Insight` (type, severity, subject, named facts, reason, recommended action), a rule contract (a pure function from a typed input snapshot and explicit thresholds to insights), the clock as an input, an evaluation query that gathers authoritative data and applies the rules, the `InsightsView` permission, and the conventions for formulas, periods and thresholds. Decide, and record in a short ADR if it is a real choice, whether insights are computed on request (the default) or materialized.
- **Acceptance criteria:** A rule is testable without a database; the result is reproducible from data and clock; facts and reason are always present; tenant scoping and permission checks are in place; no insight is persisted as a source of truth; an example rule proves the structure end to end.
- **Dependencies:** IDENTITY-002, INVENTORY-002
- **Affected area:** Application (Intelligence module), docs
- **Testing:** unit tests with a fixed clock and fixtures; tenant-isolation and authorization tests.

### INTEL-002 — Inventory risk intelligence
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** Low-stock and reorder-threshold detection; **estimated stock coverage in days** = current stock ÷ average daily quantity sold over the trailing 30 calendar days; zero sales means "no recent sales", never infinite; products at risk of running out within their lead time; suggested reorder quantity = `max(0, reorder point + average daily sales × lead-time days - current stock)` when both reorder point and lead time exist. No quantity is suggested when required inputs are missing.
- **Acceptance criteria:** Insights name the product, current stock, threshold or velocity used and window; handles no history, zero velocity, zero stock, inactive products and products without reorder settings by documented rules.
- **Dependencies:** INTEL-001, INVENTORY-003, SALES-002
- **Affected area:** Application (Intelligence)
- **Testing:** unit tests with fixed clock and fixtures for every edge case; integration test against real data; tenant isolation.

### INTEL-003 — Sales metrics and comparisons
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** Revenue and quantity for a period against the **immediately preceding period of equal length**; growth = `(current - previous) / previous × 100` when previous is non-zero; otherwise report "no comparable previous sales". Top sellers are ordered by quantity sold. Slow movers are the bottom 20% by quantity among active products with sufficient data; if fewer than 5 qualify, show the ordered list. A declining product has sales in both periods and at least a 20% quantity decline. Voided sales are excluded.
- **Acceptance criteria:** Periods, time zone (the organization's) and exclusions are explicit; results reconcile with the underlying sales; documented rounding.
- **Dependencies:** INTEL-001, SALES-002
- **Affected area:** Application (Intelligence), Api
- **Testing:** unit tests (period boundaries, time zones, voids, no data); reconciliation integration test against raw sales; tenant isolation.

### INTEL-004 — Product margin intelligence
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** Gross margin = final sale revenue − sale-line cost snapshot. Margin percentage = gross margin ÷ final sale revenue when revenue is non-zero. Margin change is compared in percentage points between equal-length periods. This is a management margin measure, not accounting-grade COGS: no FIFO, weighted-average costing or inventory valuation in V1. Visible only with `ProductViewCost`.
- **Acceptance criteria:** Margin uses snapshots only, never the product's current price; the explanation shows previous and current cost, price and both margins; missing cost or sales data is reported as "not enough data", not guessed.
- **Dependencies:** INTEL-003, SALES-001, PURCHASING-002
- **Affected area:** Application (Intelligence)
- **Testing:** unit tests (price or cost changes, no cost, zero sales); authorization for `ProductViewCost`; tenant isolation.

### INTEL-005 — Customer balance and aging intelligence
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** Outstanding balance per customer, overdue balance, **aging buckets** (0-30, 31-60, 61-90, 91+ days past the due date). Due dates come from customer-specific credit terms when present, otherwise the organization default. Customer balance insights use an explicit organization-configured materiality threshold; no hidden monetary threshold.
- **Acceptance criteria:** Ages are measured from an explicit due date; the insight lists the amount, the days and the sales behind it; payments reduce the right balances by a documented rule.
- **Dependencies:** INTEL-001, CUSTOMER-002
- **Affected area:** Application (Intelligence)
- **Testing:** unit tests (partial payments, boundaries, no terms); reconciliation with sales and payments; tenant isolation.

### INTEL-006 — Purchasing cost intelligence
- **Status:** In Review · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** Compare the latest purchase unit cost with the immediately previous purchase for the same product and supplier, and show supplier-to-supplier differences when multiple suppliers exist. Show purchase volume and recent activity. A "notable cost change" is at least 10% relative change. Visible only with `ProductViewCost`; missing history is reported as insufficient data.
- **Acceptance criteria:** Insights show the two costs, dates and suppliers; documented threshold for "notable"; handles a single purchase and missing costs.
- **Dependencies:** INTEL-001, PURCHASING-002
- **Affected area:** Application (Intelligence)
- **Testing:** unit tests; authorization; tenant isolation.

### INTEL-007 — Actionable insights and dashboard
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Backend / Frontend
- **Description:** One endpoint and one screen, "Things that need your attention": the current insights from every implemented rule, ordered by documented severity, each with its facts, reason and a recommended action that leads to the right screen (for example start a purchase order for the product). Only capabilities that exist are shown; nothing is hard-coded.
- **Acceptance criteria:** Every item on the screen can be traced to records; ordering and severity are deterministic; an empty state exists; no business rule lives in the client.
- **Dependencies:** INTEL-002, INTEL-003, FOUNDATION-007 (the margin, purchasing and customer insights join as INTEL-004..006 land)
- **Affected area:** Application, Api, Web
- **Testing:** API tests; component tests; the end-to-end journey (E2E-001) ends on this screen.

**Mapping from the earlier sequence:** old INTEL-001 (sales-velocity forecast) → INTEL-002; old INTEL-002 (stored reorder recommendations) → INTEL-008 (PRODUCT-P1); old INTEL-003 (owner approval executes a purchase order) → INTEL-009 (PRODUCT-P1); NOTIF-001 stays PRODUCT-P1.

---

# Cross-Cutting and Production Readiness (Sellable V1 Gate C)

### SECURITY-002 — Rate limiting and API hardening
- **Status:** Ready · **Priority:** SELLABLE-P0 · **Agent:** Security
- **Description:** ASP.NET Core rate limiting (strict on authentication endpoints), request size limits, security headers, CORS allowlist, HSTS.
- **Acceptance criteria:** Limits configurable; exceeded limits return 429; headers verified. Required before any external user.
- **Dependencies:** IDENTITY-001
- **Affected area:** Api
- **Testing:** integration tests for 429 and headers.

### DEPLOY-001 — Local container development
- **Status:** Ready · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** Dockerfile for the API and compose profile running API + PostgreSQL locally. No production deployment.
- **Acceptance criteria:** `docker compose up` starts a working local system.
- **Dependencies:** FOUNDATION-003
- **Affected area:** repo root
- **Testing:** start and hit health endpoint.

### DEPLOY-002 — Production deployment and configuration
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Backend / Security
- **Description:** A reproducible production deployment: container image build in CI, configuration and secret handling (no secrets in source), the explicit step that applies migrations (the app never migrates itself), same-site hosting for the cookie session (reverse proxy or shared parent domain, [ADR 0002](../decisions/0002-authentication.md)), HTTPS and HSTS, the Web build served, and a deployment document a person can follow. Choice of hosting and any paid service **needs the owner's approval** before work starts.
- **Acceptance criteria:** A fresh environment can be deployed from the repository and documentation alone; health checks report correctly; configuration is validated at start; rollback of a failed deployment is documented.
- **Dependencies:** DEPLOY-001, SECURITY-002, IDENTITY-004, FOUNDATION-007
- **Affected area:** CI, repo root, docs
- **Testing:** deploy to a staging environment and run the end-to-end journey against it.

### OBS-001 — Production observability and diagnostics
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** What an operator needs when something goes wrong: structured logs with correlation ids reaching a place they can be searched, a review that no log or error leaks a secret or personal data, readiness and liveness checks wired to the deployment, basic counters for requests, errors and business actions (sales, receipts, stock conflicts), and written guidance on what to alert on.
- **Acceptance criteria:** A failing request can be traced from a customer report to a log line by correlation id; stock conflict and error rates are visible; log review recorded.
- **Dependencies:** FOUNDATION-005, DEPLOY-002
- **Affected area:** Api, Infrastructure, docs
- **Testing:** tests for the counters and the redaction; a staging check.

### OPS-001 — Backup and recovery
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Backend
- **Description:** A documented and rehearsed backup and restore of the PostgreSQL database (frequency, retention, where copies live, who can restore), a restore into an empty environment with the stock reconciliation query run afterwards.
- **Acceptance criteria:** A restore from a real backup was performed and verified; the recovery steps and the expected data loss window are written down.
- **Dependencies:** DEPLOY-002
- **Affected area:** docs, operations
- **Testing:** the rehearsal itself, recorded.

### E2E-001 — First complete journey test
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** QA
- **Description:** An automated journey through the real application on real PostgreSQL: register, create a product, purchase and receive stock, sell, see stock decrease, see the resulting insights (low stock, sales comparison). API level first; the UI journey is added with the UI items.
- **Acceptance criteria:** The journey runs in CI and fails if any step breaks; stock reconciles at the end.
- **Dependencies:** INTEL-002, INTEL-003, PURCHASING-002, SALES-002
- **Affected area:** tests
- **Testing:** the test is the deliverable.

---

# Commercial Readiness (Sellable V1 Gate D)

### COMMERCIAL-001 — Demonstration data and walkthrough
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Backend / Frontend
- **Description:** A repeatable way to create a realistic demonstration organization (products, purchases, sales, customers, history long enough for the insights to fire) and a short scripted walkthrough of the workflow ending on "Things that need your attention".
- **Acceptance criteria:** The demo can be created in minutes in a clean environment and shows at least one insight of each implemented kind; the data is clearly demonstration data and never touches a customer organization.
- **Dependencies:** INTEL-007, SALES-003
- **Affected area:** tooling, docs
- **Testing:** the demo is created in CI and the insights are asserted.

### COMMERCIAL-002 — Onboarding and pilot readiness
- **Status:** Backlog · **Priority:** SELLABLE-P0 · **Agent:** Product
- **Description:** What a customer reads and does: a plain description (what Soforr does, who it is for, what they get), an onboarding guide (register, add products, first purchase, first sale), the known limitations of V1, and a pilot checklist for the first customer.
- **Acceptance criteria:** A person who has never seen the product can follow the guide to a first insight; limitations and support path are written down.
- **Dependencies:** COMMERCIAL-001, DEPLOY-002
- **Affected area:** docs
- **Testing:** the guide is followed once by someone who did not write it.

---

# PRODUCT-P1 — Valuable, Not Required for the First Sale

Decompose into items when their dependencies approach Done and V1 is not at risk.

| Item or epic | Depends on | Notes |
|---|---|---|
| INTEL-008 — Stored reorder recommendations | INTEL-002, PURCHASING-001 | The old INTEL-002: `ReorderRecommendation` records, idempotent regeneration, no duplicate pending per product |
| INTEL-009 — Owner approval executes a purchase order | INTEL-008, PURCHASING-002 | The old INTEL-003: `ApproveReorder` / `DismissRecommendation`, audited, executed by the application |
| NOTIF-001 — In-app notifications | INTEL-007 | Unread notifications for insights |
| EPIC 08 — Expenses (and expense intelligence) | IDENTITY-003 | Simple expense recording, totals, period comparison, threshold alerts |
| EPIC 13 — Files and receipts | IDENTITY-003, SECURITY-002 | Upload security per [security.md](../security/security.md#file-uploads) |
| EPIC 14 — Reporting beyond the V1 metrics | SALES-002, PURCHASING-002 | Read-only, deterministic |
| Product reactivation; product concurrency; audit viewing | CATALOG-003, AUDIT-001 | Open lifecycle decisions noted in [modules.md](../architecture/modules.md#catalog) |

# FUTURE — Horizon 2 and Beyond

Not started before Sellable V1 is achieved and real customers have been observed.

| Item or epic | Notes |
|---|---|
| Statistical intelligence (Layer 2) | Forecasting, demand prediction, seasonality, statistical anomaly detection |
| EPIC 12 — AI assistant (Layer 3) | Read-only tools over the deterministic services, provider abstraction; **paid provider needs approval** |
| EPIC 11 — Automation (Layer 4) | Only pre-approved safe commands; execution history |
| EPIC 15 — SaaS billing | **Needs approval** (paid services) |
| EPIC 16 — Security and compliance beyond V1 hardening | Review before wider external use |
| EPIC 17 — Observability beyond OBS-001 | Metrics and tracing platforms |
| Industry-specific modules, multi-warehouse, mobile, integrations, advanced accounting, payroll | Only when real customers demand them |

# Next Executable Items

Dependencies satisfied (status `Ready`): **AUDIT-001** (gates INVENTORY-003, PURCHASING-002, SALES-002 and IDENTITY-005), PURCHASING-001, SALES-001, SECURITY-002, SECURITY-001, FOUNDATION-007, DEPLOY-001.
