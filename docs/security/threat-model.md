# Threat Model (initial)

STRIDE-oriented, first pass for the MVP scope. To be revisited when authentication, files and AI features are designed. Principles: [security.md](security.md).

## Assets

1. Tenant business data (sales, purchases, inventory, customers, expenses)
2. User credentials and sessions
3. Financial/stock integrity
4. Uploaded files (receipts)
5. Secrets (DB, AI provider keys)
6. Audit trail
7. Availability of the service

## Actors

Anonymous internet user; authenticated user of tenant A (possibly malicious); insider with a low-privilege role; compromised account; malicious supplier/customer content (text in names, receipts); automated attackers; the AI model as a confused/manipulated component.

## Trust Boundaries

Browser ↔ API · API ↔ database · API ↔ file storage · API ↔ AI provider · CI/CD ↔ repository.

## Threats and Mitigations

| # | Threat | Category | Mitigation | Backlog |
|---|---|---|---|---|
| T1 | User of tenant A reads/writes tenant B's data via id guessing (IDOR) | Info disclosure / Tampering | Global query filter and write guard on every tenant-owned entity (convention-tested), ownership checks, 404 on foreign ids, two-tenant isolation tests | IDENTITY-003 |
| T2 | Client supplies `organizationId` to escape scope | Elevation | Tenant from the session's membership only; ids in the body or route never choose the tenant (tested) | IDENTITY-003 |
| T3 | Credential stuffing / brute force | Spoofing | Strong hashing, lockout after 5 failures and uniform sign-in answers (IDENTITY-001); per-client rate limits on the authentication endpoints, 429 with Retry-After (SECURITY-002, implemented) | IDENTITY-001, SECURITY-002 |
| T4 | Privilege escalation via missing permission check | Elevation | Default-deny fallback policy; a permission check at the top of every handler (architecture test); role matrix and per-operation authorization tests | IDENTITY-002 |
| T5 | Race conditions cause oversell / negative stock / double receive | Tampering | Transactions, concurrency tokens, idempotent receiving, concurrency tests | INVENTORY-001..003 |
| T6 | Inventory or financial history silently altered | Repudiation / Tampering | Append-only movements, voids not edits, audit events | INVENTORY-001, AUDIT-001 |
| T7 | Client-forged prices/totals | Tampering | Server computes all totals from server-side data | SALES-001 |
| T8 | Malicious file upload (script, oversized, polyglot, path traversal) | Tampering / DoS | Allowlists, size limits, generated keys, nosniff, attachment disposition | EPIC 13 |
| T9 | Prompt injection via product names, notes, receipts | Tampering / Elevation | Read-only tools, no mutation ability, tenant-scoped tools, output validation, human approval | EPIC 12 |
| T10 | AI fabricates numbers or is treated as authoritative | Integrity | Deterministic tools supply figures; show verified values | EPIC 12 |
| T11 | AI tool exposes another tenant's data | Info disclosure | Tenant from context, not model args | EPIC 12 |
| T12 | Secrets leaked in repo, logs or errors | Info disclosure | No secrets in source, secret scanning, log scrubbing, generic errors | FOUNDATION-004, SECURITY-001 |
| T13 | Stack traces / SQL details reach clients | Info disclosure | Central ProblemDetails middleware | FOUNDATION-005 |
| T14 | Injection (SQL, XSS, header) | Tampering | Parameterized EF queries, Vue output encoding, CSP, validation | ongoing |
| T15 | CSRF / token theft | Spoofing | HttpOnly cookie session so JavaScript cannot read a credential, SameSite=Lax, antiforgery token on unsafe methods (IDENTITY-001); CSP with the web client | IDENTITY-001, FOUNDATION-007 |
| T16 | Denial of service via expensive queries/uploads/AI calls | DoS | Pagination bounds, request limits, rate limiting, AI cost caps | SECURITY-002 |
| T17 | Vulnerable or malicious dependency | Supply chain | Minimal deps, pinned versions and lockfile, Dependabot, high/critical vulnerabilities fail the build (NuGet audit, npm audit) | SECURITY-001 (implemented) |
| T18 | Tampering with audit logs | Repudiation | Append-only, no update/delete paths, restricted DB role | AUDIT-001 |
| T19 | Automation executes unsafe or unintended actions | Elevation | Allow-list of safe commands, human approval for high-impact, run history | EPIC 11 |
| T20 | Personal data over-collection or leakage (customers) | Privacy | Data minimization, tenant scoping, retention policy (to define) | EPIC 07/16 |

| T21 | Backdating a sale or payment by sending an old offline event time | Tampering | The server accepts an offline event time only inside a silent gap it observed on the caller's own grant, within the 72-hour window (ADR 0008); timestamp age alone is never authorization; a refusal never falls back to server time; audited | OFFLINE-001 |
| T22 | Using another user's or organization's offline grant | Spoofing / Elevation | The grant id is an identifier, not a secret; user and organization come from the session and are compared with the stored grant; permissions are checked at replay | OFFLINE-001 |

Residual risk (accepted, bounded): a user who really stays silent can choose a time inside their own silence.

## Open Questions

- Authentication mechanism and session model (ADR needed; approval required).
- Data retention and privacy obligations by target market.
- Malware scanning for uploads.
- Backup/restore and encryption at rest (part of EPIC 18).
