# Security Principles

This is the baseline. Authentication is implemented (IDENTITY-001, see below); the rest is planned, and each area maps to backlog items (IDENTITY-*, SECURITY-*, FOUNDATION-*). Threats: [threat-model.md](threat-model.md). Rules originate in [CLAUDE.md](../../CLAUDE.md) §13–§17.

## Authentication
- Use the platform's mature mechanisms (ASP.NET Core Identity or equivalent; final choice is an ADR requiring approval per CLAUDE.md §21). No home-made crypto.
- Passwords hashed with a modern adaptive algorithm; lockout/throttling on repeated failures; short-lived access tokens; refresh/session handling defined in the IDENTITY-001 design.
- Secure cookie / token transport only over HTTPS; HSTS enabled outside development.
- **Implemented (IDENTITY-001, [ADR 0002](../decisions/0002-authentication.md)):** ASP.NET Core Identity with an `HttpOnly`, `Secure`, `SameSite=Lax` `__Host-` cookie session (no token in JavaScript or storage); passwords hashed with Identity's adaptive hasher, minimum length 12, lockout after 5 failures; identical answers for unknown address, wrong password and locked account; sliding idle timeout plus an absolute lifetime; sessions re-validated every minute so a security-stamp change ends them; antiforgery token required on register, sign-in and sign-out.
- **Not yet:** e-mail verification and password reset (IDENTITY-004), rate limiting (SECURITY-002), the CORS allowlist (with the web client). Until then the authentication endpoints are mapped only in Development.

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
| `SalesView` | GetSale, ListSales (cost prices additionally need `ProductViewCost`) | yes | yes | yes |
| `SalesCreate` | CreateSale (removes stock; a cashier needs it) | yes | yes | yes |
| `SalesVoid` | VoidSale (returns stock) | yes | yes | no |
| `InsightsView` | GetInsights (the attention list; insights that reveal costs additionally need `ProductViewCost`), GetSalesSummary (revenue, top sellers, slow movers, declining products) | yes | yes | no |
| `CustomerView` | GetCustomer, SearchCustomers (contact details additionally need `CustomerManage`) | yes | yes | yes |
| `CustomerManage` | CreateCustomer, UpdateCustomer, DeactivateCustomer; sees contact details | yes | yes | no |
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
- CI scans for committed secrets (SECURITY-001).

## API Security
- HTTPS only; CORS restricted to the known web origin; security headers; request size limits.
- Centralized error handling exposes no internals (CLAUDE.md §17).
- Versioned routes; idempotency keys for retry-sensitive writes.
- Anti-forgery/CSRF handling follows [ADR 0002](../decisions/0002-authentication.md) (Accepted: cookie session with antiforgery on unsafe methods).

## Rate Limiting
- Built-in ASP.NET Core rate limiting: strict on authentication endpoints, per-user/per-organization limits elsewhere, tighter on expensive and AI endpoints. Required before any external user (EPIC 16).

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
- Minimal dependencies; pin versions; enable Dependabot and `dotnet list package --vulnerable` in CI (SECURITY-001).

## Security Review
Every pull request records a security check (see PR template). The Security Agent reviews changes touching authN/authZ, tenancy, file handling, and AI tools.

## Reporting Vulnerabilities
Use the "Security issue" template only for non-sensitive hardening work. Report exploitable vulnerabilities privately to the project owner, not in public issues.
