# Security Principles

This is the baseline. Authentication is implemented (IDENTITY-001, see below); the rest is planned, and each area maps to backlog items (IDENTITY-*, SECURITY-*, FOUNDATION-*). Threats: [threat-model.md](threat-model.md). Rules originate in [CLAUDE.md](../../CLAUDE.md) §13–§17.

## Authentication
- Use the platform's mature mechanisms (ASP.NET Core Identity or equivalent; final choice is an ADR requiring approval per CLAUDE.md §21). No home-made crypto.
- Passwords hashed with a modern adaptive algorithm; lockout/throttling on repeated failures; short-lived access tokens; refresh/session handling defined in the IDENTITY-001 design.
- Secure cookie / token transport only over HTTPS; HSTS enabled outside development.
- **Implemented (IDENTITY-001, [ADR 0002](../decisions/0002-authentication.md)):** ASP.NET Core Identity with an `HttpOnly`, `Secure`, `SameSite=Lax` `__Host-` cookie session (no token in JavaScript or storage); passwords hashed with Identity's adaptive hasher, minimum length 12, lockout after 5 failures; identical answers for unknown address, wrong password and locked account; sliding idle timeout plus an absolute lifetime; sessions re-validated every minute so a security-stamp change ends them; antiforgery token required on register, sign-in and sign-out.
- **Not yet:** e-mail verification and password reset (IDENTITY-004). Until then the authentication endpoints are mapped only in Development, because they let anyone register. Rate limiting, security headers, HSTS and the CORS allowlist exist (SECURITY-002, below).

## Authorization
- Authenticated ≠ authorized. Every command and query checks a permission server-side.
- Roles map to permissions (Owner, Manager, Staff initially); permission checks live in Application, declared per use case.
- Default deny. Frontend hiding of controls is cosmetic only. The API has a fallback policy that requires a signed-in user; the anonymous endpoints are an allowlist guarded by a test, and every unsafe endpoint requires an antiforgery token (also tested).

### Role and permission matrix (IDENTITY-002)

Fixed roles, granted in code (`RolePermissions`). Anything not granted is denied. A permission is added with the use case that needs it, and the matrix test requires every role and permission to be decided.

| Permission | Use cases | Owner | Manager | Staff |
|---|---|---|---|---|
| `OrganizationView` | GetOrganization | yes | yes | yes |
| `OrganizationManageSettings` | UpdateOrganizationSettings | yes | no | no |
| `ProductView` | GetProduct, SearchProducts | yes | yes | yes |
| `ProductViewCost` | the cost price in a product (it is `null` without it) | yes | yes | no |
| `ProductManage` | CreateProduct, UpdateProduct, DeactivateProduct | yes | yes | no |
| `AuditView` | ListAuditEvents (the organization's audit trail) | yes | no | no |
| `InventoryView` | GetStockLevel, ListLowStock | yes | yes | yes |
| `InventoryViewHistory` | GetMovementHistory (who changed stock, when and why) | yes | yes | no |
| `InventoryAdjust` | AdjustInventory | yes | yes | no |
| `PurchasingView` | ListSuppliers, GetPurchaseOrder, ListPurchaseOrders (orders carry unit costs) | yes | yes | no |
| `PurchasingManage` | CreateSupplier, CreatePurchaseOrder, PlacePurchaseOrder, CancelPurchaseOrder | yes | yes | no |
| `SalesView` | GetSale, ListSales, GetCustomerBalance (cost prices additionally need `ProductViewCost`) | yes | yes | yes |
| `SalesCreate` | CreateSale (removes stock; a cashier needs it) | yes | yes | yes |
| `SalesVoid` | VoidSale (returns stock) | yes | yes | no |
| `SalesOverridePrice` | CreateSale with a stated line price (override or discount; always with a reason and audited) | yes | yes | no |
| `SalesSellBelowCost` | CreateSale with a line price below the product's cost price | yes | no | no |
| `InsightsView` | GetInsights (the attention list; insights that reveal costs additionally need `ProductViewCost`), GetSalesSummary (revenue, top sellers, slow movers, declining products), GetMarginSummary (also needs `ProductViewCost`: it reveals costs and margins), GetReceivables (what customers owe, aged by days past due), GetPurchaseCosts (also needs `ProductViewCost`: it reveals purchase costs) | yes | yes | no |
| `CustomerView` | GetCustomer, SearchCustomers (contact details additionally need `CustomerManage`) | yes | yes | yes |
| `CustomerManage` | CreateCustomer, UpdateCustomer, DeactivateCustomer; sees contact details | yes | yes | no |
| `PaymentsRecord` | RecordPayment (money received against a sale on account; recorded, never processed) | yes | yes | yes |
| *(offline)* | IssueOfflineGrant and RecordOfflineContact need `SalesCreate` **or** `PaymentsRecord`; the grant authorizes nothing by itself and each replayed sale or payment is checked against its own permission at replay time (ADR 0008) | yes | yes | yes |
| `PaymentsReverse` | ReversePayment (takes a payment back, which puts it back on what a customer owes) | yes | yes | no |
| `PurchasingReceive` | ReceivePurchase (adds stock; a receipt id can never be reused for a different receipt) | yes | yes | no |

Outcomes: not signed in → 401; signed in without a membership or without the permission → 403; another organization's resource → 404 (after the permission check). Registration, sign-in and sign-out are the only operations that need no permission. Planned permissions (`Members.Manage`, price override and discount permissions with SALES-004) are added with their features.

## Tenant Isolation (critical boundary)
- Organization identity derives from the authenticated principal, never from client-supplied ids.
- EF global query filter on `OrganizationId` + explicit ownership verification for referenced ids in commands.
- Cross-tenant access returns 404. Automated tests for every tenant-owned endpoint are mandatory.
- Files, cached data, background jobs and AI tools are tenant-scoped too.
- Changing this design requires human approval.
- **Implemented (IDENTITY-003):** the global query filter, the write guard on save, the convention tests and the two-tenant fixture, as described in [ADR 0002](../decisions/0002-authentication.md). Only the sign-in lookup of the membership bypasses the filter. Writes are unrestricted only while there is no organization (registration creates the tenant); the Organization keeps its own filter as defense in depth (ADR 0002, decisions A and B).

## Input Validation
- Validate all input at the API boundary (shape) and in Application (rules); Domain enforces invariants.
- Bounded string lengths, numeric ranges, page sizes, collection sizes. Reject, do not silently coerce.
- Parameterized queries only (EF Core); no string-concatenated SQL.

## File Uploads
- Enforce size limits, extension and content-type allowlists, and sniff magic bytes server-side.
- Generate storage keys; never use user-supplied file names as paths. Store outside the web root.
- Serve with correct `Content-Type`, `X-Content-Type-Options: nosniff`, and `Content-Disposition: attachment` for non-images; authorize every download by tenant.
- Consider malware scanning before files are opened by other users (deferred; decide before EPIC 13 ships).

## Secrets
- None in source control, images or logs. Use user-secrets locally, environment/secret store in deployment. `.env` is git-ignored.
- Rotate on suspicion; provider API keys (e.g. AI) scoped and budget-limited.
- **Secret scanning (SECURITY-001):** a `secret-scan` job in CI runs gitleaks over the whole git history on every pull request (default rules plus `.gitleaks.toml`; an allowance there needs a written reason and review). Enable GitHub's own **secret scanning and push protection** in the repository settings too: it blocks a push that contains a known secret format before it reaches the history. Locally, an optional pre-commit check is described in the [development guide](../engineering/development.md#secret-scanning-and-vulnerable-packages). If a secret is ever committed, rotate it first; deleting it from history does not make it safe.

## API Security
- HTTPS only; CORS restricted to the known web origin; security headers; request size limits (SECURITY-002, implemented in `ApiHardening.cs`, configured in the `Hardening` section and validated at start).
  - **Security headers** on every response, including errors and 429: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cross-Origin-Resource-Policy: same-origin`, `Permissions-Policy` denying camera, microphone and geolocation, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` (the API serves data, not pages), and `Cache-Control: no-store` on `/api`.
  - **HSTS** outside Development (`Hardening:HstsDays`, default 365, includeSubDomains); browsers only honor it over HTTPS. The HTTPS redirect stays.
  - **Request size:** bodies over `Hardening:MaxRequestBodyBytes` (default 1 MiB) get 413 `request.too_large`, decided from the declared length before the body is read and enforced by the server for chunked bodies.
  - **CORS:** no origin is allowed by default (the Web app is same-site, ADR 0002). `Hardening:CorsAllowedOrigins` is an explicit list of origins (scheme://host[:port], no path, never a wildcard); an allowed origin gets credentials, the methods GET, POST, PUT, PATCH, DELETE and only the `Content-Type` and `X-CSRF-TOKEN` headers.
  - A setting that makes no sense (a wildcard or path in an origin, a limit below 1) stops the application at start.
- Centralized error handling exposes no internals (CLAUDE.md §17).
- Versioned routes; idempotency keys for retry-sensitive writes.
- Anti-forgery/CSRF handling follows [ADR 0002](../decisions/0002-authentication.md) (Accepted: cookie session with antiforgery on unsafe methods).

## Rate Limiting
- Built-in ASP.NET Core rate limiting: strict on authentication endpoints, per-user/per-organization limits elsewhere, tighter on expensive and AI endpoints. Required before any external user (EPIC 16).
- **Implemented (SECURITY-002):** fixed one-minute windows per client address, no queueing. The authentication endpoints (register, login, logout) share a strict limit (`Hardening:AuthPermitsPerMinute`, default 10); the whole API has a general limit (`Hardening:GlobalPermitsPerMinute`, default 600); health checks are never limited. Over the limit: 429 with `Retry-After` and a problem body with code `rate_limit.exceeded`.
- **Offline endpoints (OFFLINE-001):** the grant and contact endpoints share one named policy (`Hardening:OfflinePermitsPerMinute`, default 60 per minute per client address), part of the same hardening configuration.
- **Known limits, decided later:** the client address is the connection's remote address. Behind a reverse proxy it is the proxy's address until forwarded headers are configured from a trusted proxy list (DEPLOY-002); until then do not deploy behind a proxy without that step, or every client shares one limit. Limits are per application instance and in memory; with several instances the effective limit is per instance. Per-user and per-organization limits, and tighter limits for expensive and AI endpoints, are added with those endpoints.

## Auditability
- Append-only audit events for authentication events, permission changes, inventory adjustments, purchase approvals, reorder approvals, voids, and destructive operations: who, what, when, organization, correlation ID.
- Audit records are not editable through the application, and not through the database either: a trigger refuses `UPDATE`, `DELETE` and `TRUNCATE` on the table even from raw SQL ([modules.md](../architecture/modules.md#audit)). Only the Owner may read the trail (`AuditView`).
- **Recorded today (AUDIT-001):** registration, the Owner role assignment, successful sign-in, sign-out, and the moment an account is locked out. A refused sign-in attempt is not recorded (it is logged), so an anonymous caller cannot make the system write unlimited rows; an unknown address records nothing (there is no organization to attach it to). Events hold identifiers, never an e-mail address or a password. Later items record their own actions (inventory adjustments, purchase receipts, role changes, ...) through the same `IAuditLog`.

## AI Security
- See [ai-architecture.md](../architecture/ai-architecture.md): tools only, read-only, tenant-scoped, no direct DB access, untrusted-input handling (prompt injection), no secrets or excess personal data sent to providers, cost caps, audited tool calls.
- Approvals for AI-suggested actions are made by an authenticated human and executed as ordinary authorized commands.

## Financial Data Integrity
- Computed by deterministic code; stored with decimal precision; never generated by an LLM.
- Financial and stock records are append-only or voided/reversed by new records; no silent overwrites.
- Transactions and concurrency control protect multi-row operations.
- No payment processing or automatic financial transactions without human approval and design review.

## Dependencies and Supply Chain
- Minimal dependencies; pin versions (NuGet through central package management, npm exact versions and a lockfile).
- **Known-vulnerable packages fail the build (SECURITY-001):** NuGet audits direct and transitive packages at restore (`NuGetAuditMode=all`, `NuGetAuditLevel=high` in `Directory.Build.props`), and NU1903 (high) and NU1904 (critical) are errors, locally and in CI. The `web` job runs `npm audit --audit-level=high`. Lower severities are visible in Dependabot but do not fail the build. Offline, NuGet reports NU1900 (it could not reach the vulnerability database), which does not fail a local build; CI has network access.
- **Dependabot** (`.github/dependabot.yml`) opens weekly update pull requests for NuGet, npm (`src/Soforr.Web`) and GitHub Actions; they go through the same CI and review. Enable Dependabot security updates in the repository settings as well.
- The first `npm audit` run found a critical advisory in the test runner's dependency chain (tinypool, through Vitest 3); Vitest was upgraded to 5.0.3, which clears it.

## Security Review
Every pull request records a security check (see PR template). The Security Agent reviews changes touching authN/authZ, tenancy, file handling, and AI tools.

## Reporting Vulnerabilities
Use the "Security issue" template only for non-sensitive hardening work. Report exploitable vulnerabilities privately through GitHub's **Report a vulnerability** (private advisory), as described in [SECURITY.md](../../SECURITY.md), not in public issues. Private vulnerability reporting must be enabled in the repository settings for that link to work.
