# 0008 — Offline authorization boundary

- Status: Proposed
- Date: 2026-10-09
- Deciders: project owner

Detail of [ADR 0007](0007-offline-resilience.md), which left the mechanism to OFFLINE-001 and required it to be written up and reviewed before implementation. No code changes accompany this record.

## Context

ADR 0007: an offline sale keeps the time it happened on the device (`occurredAtUtc`), but *timestamp age is not authorization*. A client can send any timestamp inside 72 hours. The server needs evidence, established by itself, that an operation really was created during a period when the device could not reach it, and it must refuse anything else without falling back to server time.

Constraints (ADR 0002 and 0007): the cookie session stays the only credential; no long-lived bearer secret; nothing credential-like is stored in the browser; the server derives user and organization from the session; the client is authoritative for nothing. The repository has no offline-grant mechanism to reuse.

## What can and cannot be guaranteed

The server never sees the device's clock or its connection. Two kinds of evidence are available to it: **what it observed** (when this user's device was last in contact) and **what the user claims**. An honest user who really was offline cannot be told apart from a dishonest user who stays silent on purpose and sells during that silence. So the strongest honest guarantee is:

> An offline event time is accepted only inside a period during which the server observed no contact from that user's device, only within the 72-hour policy after the last contact, and never before the server last saw the device online.

What this prevents: backdating into a period when the server saw the device online (the 14:00-sold-as-yesterday-10:00 attack, when the device was in contact yesterday evening), another user's or organization's grant, future times, and times beyond the window. What it cannot prevent: a user who really goes silent choosing any time inside their own silence. That residual risk is bounded by the length of the silence and by the existing server rules on replay (permission, stock, price), and it is audited (below).

## Options considered

### Option A: server-observed contact (recommended)

The server records *contact observations* for an **offline grant** and accepts an event time only inside a silent gap.

- **Offline grant.** After a successful online validation the client asks `POST /api/v1/offline/grants` (authenticated, antiforgery, rate-limited, needs `SalesCreate`). The server creates a grant row and returns its server-generated id and the server time. The grant id is an **identifier, not a secret**: it is only meaningful inside an authenticated session of the same user and organization (checked against the stored `UserId` and `OrganizationId`; the organization comes from the session, never from the request), so a copied id is useless to anyone else and carries no authority by itself.
- **Contact.** While the app is open and online it reports contact every 5 minutes with `POST /api/v1/offline/grants/{id}/contact` (cheap; also what the connectivity check can use). The server stores the observation time. The grant lives while contact continues; a grant with no contact for the policy window expires; an absolute maximum age (default 30 days) forces a new grant, and a user or membership that is deactivated revokes its grants.
- **Silent gap.** The time between two consecutive contact observations of a grant, if longer than `MinGap` (default 10 minutes, twice the contact interval), or the time since the last observation (an open gap).
- **Acceptance of an offline `CreateSale` / `RecordPayment` event time `t`** (all server-side, on the authenticated session):
  1. the grant exists in the caller's organization, belongs to the caller, is not revoked;
  2. `t` is not in the future (clock-skew allowance, default 2 minutes) and `t ≤ receivedAt`;
  3. `t` lies inside a silent gap of that grant, and `t ≤ gapStart + 72 hours` (the policy window, configurable within bounds);
  4. only then the sale is dated `t`; the server stores its own `receivedAtUtc` and the grant id.
- **Order does not matter.** Whether the client syncs the operations before or after reporting contact again, the gap is already recorded by the contact call that closes it, so a legitimate operation is not invalidated by reconnecting.
- **Refusal.** An event time outside every silent gap, or past the window, or with no/foreign/revoked grant, is refused with a specific error (409/422 `sale.event_time_unverifiable`); nothing is recorded, no id is reserved, the operation becomes a visible conflict. **Never** silently dated at server time. The resolution is an explicit, audited user action: an authorized user may re-submit it as a sale dated at the moment of synchronization (the flag is kept on the sale) or discard it with a reason. This also covers an honest sale made during an outage shorter than `MinGap`, which the server cannot verify.
- **Audit.** Issuing a grant, a refusal, and every accepted offline sale (with the gap length and receipt delay) are audit events or structured log entries; no personal data.
- **Cost.** One table for grants, one append-only table of contact observations (about 12 per hour per active device, pruned with the grant), three small endpoints, configuration, tests.

### Option B: grant window only (simpler, weaker)

The grant records only `IssuedAtUtc` and an expiry (72 hours later); no contact observations. An event time is accepted inside `[IssuedAtUtc, ExpiresAtUtc]`. Simple, but a device that stays online can still send sales dated anywhere inside the window, because the server has no evidence about contact. It narrows the attack to "after the grant was issued" but does not distinguish offline from online, which is the point of the requirement. **Not recommended**; listed because it is the minimum that is still server-established.

### Rejected

- A signed token or device certificate carrying the window: more cryptographic machinery, a bearer artifact that outlives the session, key management, and it still proves nothing about contact. Not needed.
- Trusting the client clock or `navigator.onLine`: the client is not authoritative.
- A long-lived offline credential: a permanent bypass (ADR 0007).

## Decision requested

Adopt **Option A**. The data model, the exact error codes and the parameter defaults above are to be fixed in OFFLINE-001 and may be adjusted by it, provided the guarantee statement and the refusal rule stay as written.

## Consequences

- Backdating into a period of observed contact is refused; the 72-hour policy is enforced by the server, not by the client.
- Short outages (under `MinGap`) produce a conflict instead of a silent success; the resolution is explicit. If this proves too noisy for the first customer, `MinGap` and the contact interval are configuration, but lowering them is a security trade-off.
- The client must report contact while open and online (OFFLINE-002); a closed browser is silent time, which is correct.
- Event time is now meaningful business data with provenance (grant, gap, receipt delay), useful for audit and for spotting abuse.
- Residual risk stated honestly: a user can choose any time inside their own real silence.

## Tests required by OFFLINE-001

The backdating attack (sale made now, claimed at a time the server saw contact); a legitimate sale in a real gap, synced before and after the closing contact call; the future-time and window-end bounds (exact boundaries); a foreign user's grant, another organization's grant, a revoked grant, no grant; refusal reserves nothing and never re-dates; the explicit re-submit path; replay of an accepted offline sale is idempotent; tenant isolation of grants and observations; rate limiting of the contact endpoint; pruning; migrations.
