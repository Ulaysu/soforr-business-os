# 0002 — Authentication, authorization and tenant isolation

- Status: **Accepted** (approved by the project owner, 2026-09-30)
- Date: 2026-09-30
- Deciders: project owner

## Context

The Vue SPA calls the ASP.NET Core API. The product is multi-tenant SaaS: a breach across tenants is the worst failure. CLAUDE.md §13–14 require server-side enforcement of authorization and tenancy. This ADR keeps three concerns separate, because they fail differently and are tested differently:

| Concern | Question | Enforced by |
|---|---|---|
| **Authentication** | Who is calling? | Identity system (this ADR §1) |
| **Authorization** | May this user perform this action? | Role → permission checks in Application (§2) |
| **Tenant isolation** | Which organization's data may this user touch? | Organization context + EF filters + ownership checks (§3) |

Being authenticated grants no permission; having a permission grants no access outside one's own organization.

## 1. Authentication

### Evaluation

| Option | Notes |
|---|---|
| **ASP.NET Core Identity** | Platform component: password hashing, lockout, security stamp, email-confirmation and password-reset tokens, EF Core store. Avoids home-made credential code. Its `IdentityUser` is an Infrastructure type; Domain does not see it. |
| Custom user/password code | Rejected: security-critical, no benefit. |
| External IdP (Auth0, Entra, etc.) | Paid/external dependency; needs approval; unnecessary for MVP. Revisit for SSO. |

Token transport for a Vue SPA:

| Option | Pros | Cons |
|---|---|---|
| **A. HttpOnly cookie session** (Identity cookie, `Secure`, `SameSite=Lax/Strict`) | No credential in JS reach (XSS cannot steal it); built into ASP.NET Core; revocation via security stamp; simplest | Requires SPA and API on the same site (same origin or sibling subdomains); CSRF must be handled; awkward for future non-browser clients |
| B. JWT access token in `localStorage` | Easy | Any XSS steals it; revocation hard. **Rejected.** |
| C. Short-lived JWT in memory + rotating refresh token in HttpOnly cookie | Works cross-origin and for future native clients; XSS cannot steal the refresh token | Much more machinery: token issuing, refresh rotation with reuse detection, hashed refresh-token table, revocation by token family, clock/expiry handling |

### Decision: Option A for the MVP

The only client is the first-party SPA, which we control and can serve same-site (Vite dev proxy locally; reverse proxy or shared parent domain in deployment). Option A is the least code with the strongest browser-side security. Mobile apps are outside the MVP (CLAUDE.md §24). If a non-browser client is approved later, add Option C behind the same `ICurrentUser` abstraction via a new ADR; Application/Domain do not change.

Details:
- **Storage:** cookie only; nothing in `localStorage`/`sessionStorage`.
- **CSRF:** `SameSite=Lax` (Strict if UX allows), plus ASP.NET Core antiforgery token required on unsafe methods (SPA reads a non-HttpOnly antiforgery cookie/endpoint and sends it as a header), plus CORS allowlist of the single web origin with credentials.
- **Logout/revocation:** logout clears the cookie; "sign out everywhere", password change and role change update the Identity **security stamp**; stamp validation runs on a short interval (recommended: 1 minute) so revoked sessions die quickly. Deactivating a user does the same.
- **Session lifetime:** sliding expiration with an absolute maximum (values decided in IDENTITY-001; defaults conservative).
- **Password reset & email verification:** Identity token providers with short-lived, single-purpose tokens. Requires an `IEmailSender` port in Application. MVP ships a development sender that logs the link; a real provider is a paid external service and needs approval. Email verification is **required before any external user** and before enabling reset for real addresses. Tracked as IDENTITY-004 (off the first-slice critical path).
- **Passwords:** Identity's default adaptive hasher, minimum length per current NIST guidance (length over composition rules), lockout on repeated failure, throttling via rate limiting (SECURITY-002).
- **Registration:** creates User + Organization + Owner membership in one transaction.

## 2. Authorization

- **Model:** fixed roles for the MVP: Owner, Manager, Staff. Each role maps to a set of named permissions (e.g. `Products.Manage`, `Inventory.Adjust`, `Purchasing.Receive`, `Sales.Create`, `Sales.Void`, `Members.Manage`). The mapping lives in code (Application), versioned and testable. Custom/editable roles are out of MVP.
- **Where enforced:** each command/query declares and checks its required permission in Application through `ICurrentUser`. The API has a **default-deny** fallback policy (`RequireAuthenticatedUser`); anonymous endpoints are explicit exceptions. Frontend hiding of controls is cosmetic.
- **Outcomes:** unauthenticated → 401; authenticated without permission → 403.
- **Invariant:** an organization always keeps at least one Owner.
- **Testing:** matrix test (operation × role) per protected operation.

## 3. Organization membership and tenant isolation

- **Membership:** `Membership(UserId, OrganizationId, Role)`. **MVP: one organization per user** (unique on `UserId`). Multi-organization users would require an "active organization" selection design and is deferred by a future ADR.
- **Organization context:** derived server-side from the authenticated user's membership (loaded at sign-in into the auth ticket, re-validated with the security stamp). It is **never** taken from route, query or body values. `IOrganizationContext.OrganizationId` is the only source used by Application and Infrastructure.
- **Read isolation:** EF Core global query filter on `OrganizationId` for every tenant-owned entity, sourced from `IOrganizationContext`. A convention test fails if a tenant-owned entity lacks the filter.
- **Write isolation:** `OrganizationId` is set from the context on create, never from input. Commands that accept foreign ids (e.g. `ProductId` on a sale line) resolve them through the filtered queryable, so a foreign id behaves as not found.
- **Responses:** resources in other tenants return 404 (never 403), so existence does not leak.
- **Background/AI/file paths:** jobs and AI tools receive an explicit organization context; files are checked on every download.
- **Defence in depth considered:** PostgreSQL row-level security. Not adopted for the MVP (extra operational complexity, connection-context handling); revisit before hosting many external tenants (EPIC 16).
- **Tests:** a two-tenant integration fixture; every tenant-owned endpoint has cross-tenant read/write/id-injection tests (IDENTITY-003, mandatory).

## Consequences

- IDENTITY-001 is implemented as Option A; scope excludes email delivery and reset (IDENTITY-004).
- Frontend and API must be deployed same-site; documented in deployment work (EPIC 18).
- JWT is deferred until a real mobile/external API requirement exists; adding such clients later needs Option C and a new ADR.
- Identity's tables (users, stamps) live in Infrastructure; Domain owns `Membership`, `Role`.

## Implementation notes (IDENTITY-001)

These record how the decisions above were implemented; they do not change them.

- **Cookie:** `__Host-soforr.session` (the prefix makes browsers accept it only if it is `Secure`, has `Path=/` and no `Domain`), `HttpOnly`, `Secure`, `SameSite=Lax`, not persistent.
- **Session lifetime (defaults, configurable under `Session:`):** idle timeout 1 hour (sliding), absolute lifetime 12 hours. Identity's cookie only slides, so the sign-in time is kept in the ticket and checked on every request; a session older than the absolute lifetime is rejected however active it is.
- **Revocation:** the security stamp and the membership are re-validated every minute (`Session:SecurityStampValidationInterval`). Re-validation regenerates the principal, so a changed role or a removed membership reaches an existing session within that interval; a changed security stamp ends it.
- **Claims:** the user id, the organization id (`soforr.organization`) and the role come from the stored membership at sign-in and at every re-validation. They are never taken from the request.
- **Passwords:** Identity's PBKDF2 hasher; minimum length 12, at most 128, no composition rules; lockout after 5 failed attempts for 15 minutes.
- **Sign-in answers:** an unknown address, a wrong password and a locked-out account return the same `401 identity.invalid_credentials`, and an unknown address does the same hashing work as a wrong password, so neither the response nor its timing says which addresses exist. Registration necessarily reveals that an address is taken (`409 identity.email_taken`); e-mail verification (IDENTITY-004) is what addresses that.
- **Antiforgery:** `GET /api/v1/auth/antiforgery` sets an `HttpOnly`, `SameSite=Strict` antiforgery cookie and returns the token and the header name to send (`X-CSRF-TOKEN`). The token is bound to the identity it was issued for, so the SPA fetches a new one after signing in or out. Register, sign-in and sign-out require it. IDENTITY-002 applies the same filter to every unsafe endpoint.
- **Not yet in place:** e-mail verification (IDENTITY-004). Rate limiting and the CORS allowlist exist (SECURITY-002). Until e-mail verification exists the authentication endpoints are mapped only in Development, because they let anyone register.
- **Infrastructure now references the ASP.NET Core shared framework** (a `FrameworkReference`), because Identity's sign-in and cookie handling live there. Application and Domain are unchanged and still reference no web or Identity type: Application sees only its own ports (`IUserAccounts`, `ISessionService`, `ICurrentUser`).

## Implementation notes (IDENTITY-002)

- **Permissions** are the `Permission` enum and the role mapping is `RolePermissions` (both in Application, in code). The matrix is documented in [security.md](../security/security.md) and a test fails if a role or permission is added without a decision in it.
- **The check** is `ICurrentUser.Require(permission)` at the top of each handler, returning `identity.not_authenticated` (401) or `identity.forbidden` (403). Signed in without a membership is 403. An architecture test fails when a handler does not take `ICurrentUser`, except the two that run before sign-in.
- **Default deny:** the API has a fallback policy `RequireAuthenticatedUser`. Anonymous endpoints are an explicit allowlist guarded by a test (the authentication endpoints, the health checks, the OpenAPI document). A catch-all endpoint keeps unknown routes a 404, because the fallback policy would otherwise answer 401 to every unknown path.
- **Antiforgery:** `RequireAntiforgery()` is applied to the authentication and organization groups; it checks only unsafe methods. A test requires it on every unsafe endpoint.
- **Foreign organizations are 404, not 403:** the organization handlers compare the requested id with the caller's own organization and answer not found otherwise. The permission is checked first, so a role without the permission gets 403 whatever the id. The general mechanism (query filters on every tenant-owned entity) is IDENTITY-003.

## Implementation notes and decisions (IDENTITY-003)

Section 3 is implemented as written. Two choices inside it were confirmed by the project owner and are recorded here as decisions.

**Mechanism**

- **Read isolation:** `AppDbContext` takes `IOrganizationContext` and gives `Organization` (by its own id) and `Membership` (by `OrganizationId`) a global query filter. A convention test reads the EF model and fails when an entity with an `OrganizationId` has no filter, and when any entity is neither filtered nor on a short reviewed list of non-tenant entities (the Identity credential tables: users, claims, logins, tokens).
- **Write isolation:** `AppDbContext` refuses to save an added, changed or deleted tenant-owned row whose organization is not the current one (`TenantIsolationException`, a defect, not a client error).
- **Handlers** still compare the requested organization id with the caller's own and answer 404 (above); the filter is the second line. Every future tenant-owned entity resolves foreign ids through the filtered queryable, so a foreign id behaves as not found.
- **Not adopted:** PostgreSQL row-level security (unchanged; revisit before hosting many external tenants).

**Decision A: writes are not restricted when there is no current organization**

- *Invariant:* **no organization context means no tenant data is readable through normal queries (the filters match nothing); once an organization context exists, every tenant-owned write must belong to that organization.**
- Registration creates the first organization and its Owner membership before any tenant context exists, so a write guard that also rejected "no organization" would make registration impossible. The guard therefore applies only when a current organization exists, and it is not weakened in that case.
- Why no explicit bypass or "registration scope" object: the only code that runs without a signed-in user is Register and SignIn, and an architecture test (`AuthorizationConventionTests`) fails when any other handler does not take `ICurrentUser`. An extra mechanism would add machinery without adding protection.
- *Reconsider when:* a handler other than Register needs to run without an organization and write tenant-owned data (for example a background job or an invitation-acceptance flow); that should then get an explicit, narrowly scoped mechanism through a new decision, not a widening of this one.

**Decision B: `Organization` keeps its own query filter (`Id == current organization`)**

- *Why:* the `Organization` is itself tenant-scoped. The handlers check ownership explicitly, but the filter is defense in depth: it makes sure an ordinary EF query cannot return another tenant's organization, including from code written later that forgets the handler check.
- It **does not replace** the permission check and the own-organization check at the application boundary; those remain required (ADR 0003, and the `AuthorizationConventionTests` rule).
- *Reconsider when:* the Organization needs to be read without a current organization other than for sign-in (for example a public organization page, or platform administration across tenants). That is a new use case and needs its own decision.

**Sign-in lookups bypass the filter on purpose.** The claims factory and the sign-in response read the membership with `IgnoreQueryFilters()` because they are what establishes the organization, so there is none to filter by yet. These are the only two such reads in the source, and an architecture test (`TenantFilterBypassTests`) fails if any other file uses `IgnoreQueryFilters`.

## Decisions approved by the owner

1. Option A (cookie session) over B/C for the MVP.
2. One-organization-per-user for the MVP.
3. ASP.NET Core Identity (platform component; no new paid service).
4. Fixed roles/permissions in code for the MVP.
