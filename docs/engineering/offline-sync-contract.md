# Offline synchronization contract (server side, OFFLINE-001)

The backend contract the offline client (OFFLINE-002 onward) builds on. Decisions: [ADR 0007](../decisions/0007-offline-resilience.md), [ADR 0008](../decisions/0008-offline-authorization-boundary.md). Nothing here lets the client decide anything: the server is authoritative for the user, the organization, permissions, the event time, the product and customer state, the stock, the price and the financial rules.

## The honest guarantee

The server cannot prove that a device was physically offline. It records when it **observed contact** from a user's device and vouches for an offline event time only inside a **silent gap** it observed. A user who really goes silent can choose any time inside their own silence; that residual risk is bounded by the 72-hour window and by all the other server checks, and it is audited. What the server does refuse: any time at which it saw the device in contact (the backdating attack), any time outside the grant, in the future or past the window, and any grant that is not the caller's. *Timestamp age is not authorization.*

## Endpoints

All require a signed-in session and an antiforgery token (unsafe methods), and share the offline rate limit (`Hardening:OfflinePermitsPerMinute`, default 60 per minute per client address; the existing hardening mechanism, 429 with `Retry-After`).

| Endpoint | Purpose |
|---|---|
| `POST /api/v1/offline/grants` | Issue the caller's offline grant. 201 with the grant. Replaces (supersedes) the caller's previous grant. |
| `POST /api/v1/offline/grants/{id}/contact` | Report contact; the server records its own clock. 200 with the grant. |
| `POST /api/v1/sales` | Existing. For an offline sale also send `occurredAtUtc`, `offlineGrantId` and `catalogPriceShown` on every line. |
| `POST /api/v1/sales/{id}/payments` | Existing. For an offline payment also send `paidAtUtc` and `offlineGrantId`. |

The **grant id is an identifier, not a secret**. It authorizes nothing without the caller's session; the user and organization are taken from the session and compared with the stored grant, never from the request. The response carries `grantId`, `issuedAtUtc`, `lastContactAtUtc`, `expiresAtUtc` (last contact plus the window, never past the absolute expiry), `absoluteExpiresAtUtc`, `windowSeconds`, `contactIntervalSeconds` (how often to report contact; advisory) and `serverTimeUtc`.

**Permission.** (The built-in roles currently hold both permissions below; the checks are per capability and independent, so adding a role with only one of them needs no change.) A grant may be issued to, and contact reported by, a user who may record sales or payments (`SalesCreate` or `PaymentsRecord`). The grant authorizes nothing else: an offline sale is still checked against `SalesCreate`, an offline payment against `PaymentsRecord`, and an override against `SalesOverridePrice` / `SalesSellBelowCost`, each with the user's **current** membership at replay time. A role change, a removed membership or a deactivated user is therefore respected on replay (no session means no replay).

## Event-time rule

`t` = `occurredAtUtc` (sale) or `paidAtUtc` (payment, when an offline grant is named); `received` = the server clock when the request is handled.

- **Silent gap:** between consecutive contacts `a` and `b` of the grant, `[a, b)` when `b - a` is longer than `MinGap` (default 10 minutes; the issuing is the first contact). After the last contact `c` the open gap is `[c, end]` with `end = min(received, supersededAt, absoluteExpiry)`, counted only if longer than `MinGap`. Lower bound **inclusive**; the closing contact's instant is **exclusive** (the device was in contact then); the open gap's end is **inclusive**.
- **Window:** `t - gapStart <= Window` (default 72 hours, configurable from 1 hour to 7 days). Exactly the window is valid; one tick more is `offline.event_time_outside_window`.
- **Future:** `t <= received + ClockSkew` (default 2 minutes), else `offline.event_time_in_future`. The skew only separates "the device clock is ahead" from the other refusals. It never lengthens a gap or the window: an open gap ends at receipt, so a time after `received` is `offline.event_time_unverifiable`.
- **Order of checks:** grant found and the caller's, revoked, future, past absolute expiry, after being superseded, then gap and window.
- **Order of events does not matter:** a sale synchronized before the contact that closes its gap, or after it, gets the same answer, and a contact never removes evidence of an earlier gap. Contacts are append-only.
- **Short blips:** a silence not longer than `MinGap` is not a gap, so a sale made then is refused and needs the explicit resolution below. This is the price of not trusting short silences.
- Times are truncated to microseconds (PostgreSQL precision) before they are judged, stored and compared on a repeat.

An offline sale is stored with `CreatedAtUtc` = the event time (so the credit due date, margins and insight periods follow the real sale), `ReceivedAtUtc` = the server clock, and `OfflineGrantId`. Stock movements and the audit events carry the server's receipt time. A sale without `occurredAtUtc` is an ordinary online sale at server time, exactly as before.

**A refusal never falls back to server time.** Nothing is recorded, no id is reserved, and the client must resolve it explicitly (below). A refusal writes an `offline.event_refused` audit event (grant, times, code; no personal data).

## Price

Every line of an offline sale states `catalogPriceShown`, the catalog price the cashier saw. If it differs from the product's current catalog price the sale is refused with 409 `sale.catalog_price_changed` (nothing recorded, no id reserved). Only the **catalog** price is compared. The final `unitPrice` of an authorized SALES-004 override or negotiated price is never evidence of drift, and keeps its permissions and reason rules. A resolution is a new submission with the price the customer was shown as a reasoned override, or at the current price. (A sale that carries an override and whose catalog price also changed is judged by the catalog comparison, and the override is then re-submitted with the new catalog price shown; it is never silently altered.)

## Idempotency

Unchanged and reused: the sale id and the payment id. A repeat of the same request returns 200 `alreadyRecorded` and changes nothing, including a repeat after a lost response, after a later contact, and after a catalog price change; for an offline sale "the same request" includes the event time and the grant. The same id with a materially different request (another time, quantity, price, or an online request for an offline sale) is 409 `sale.id_conflict` / `sale.payment_id_conflict`. The repeat check runs **before** the event time is judged, so a replay is never re-evaluated against evidence that has since grown or been pruned. A refused request reserves nothing, so a corrected request under the same id succeeds. Five simultaneous identical replays have exactly one business effect.

## Error taxonomy

A client classifies every response by status and `code`:

| Class | Responses | What the client does |
|---|---|---|
| **Authentication required** | 401 | Pause; resume after the original user signs in again. |
| **Retry** | no network, timeout, 429 (after `Retry-After`), 502/503/504, other 5xx, `offline.concurrency_conflict` | Retry with backoff; the id makes it safe. |
| **Terminal** | 400 validation (`offline.grant_required`, `offline.event_time_required`, `offline.grant_invalid`, `offline.event_time_in_future`, `sale.timestamp_not_utc`, `sale.catalog_price_required`, other field errors), 403 forbidden, 404 (`offline.grant_not_found`, product/customer/sale not found) | Do not retry the same request; fix it or surface it. A missing or foreign grant needs a new grant and an explicit resolution. |
| **Conflict, needs attention** | 409 (`offline.event_time_unverifiable`, `offline.event_time_outside_window`, `offline.grant_revoked`, `offline.grant_expired`, `offline.grant_superseded`, `sale.catalog_price_changed`, `sale.id_conflict`, `sale.payment_id_conflict`) and the business 422s (`stock_level.insufficient_stock`, `sale.payment_exceeds_outstanding`, ...) | Stop retrying; a person decides. |

For `offline.grant_*` on **contact**, ask for a new grant. For a refused **operation** (`offline.event_time_*`, `offline.grant_*`) the resolution is a deliberate action by an authorized user: submit it as an ordinary online sale or payment dated at synchronization (the client says so; the server does not do it silently), or discard it with a reason. Flagging such a sale is part of the conflict experience (OFFLINE-006).

## Grants

- **One active grant per user** (enforced by a partial unique index). Issuing a new one supersedes the previous: it stops taking contact and vouches only up to the moment it was replaced. Two simultaneous issuances leave exactly one active.
- **Lifetime:** at most `GrantMaxAge` (default 30 days), and contact is refused after a silence longer than the window (`offline.grant_expired`; ask for a new grant). A **revoked** grant vouches for nothing. Revocation is a Domain operation used when a user or membership is removed (IDENTITY-005 wires it to member management); until then a removed user simply has no session.
- **Evidence retention and pruning:** a grant and its contacts are kept until the last time they could vouch for anything (the last contact plus the window, never past its absolute expiry, replacement or revocation) plus `EvidenceRetention` (default 90 days). A replay after that is refused as `offline.grant_not_found`. Pruning runs **inside every grant issuance, for that organization only**, through the tenant-filtered context: no filter bypass and no background job. An organization that issues no grants grows no new evidence. Sales keep the grant id as provenance without a foreign key, so pruning never touches them.
- **Audit:** `offline.grant_issued`, `offline.event_refused`, `offline.sale_accepted` and `offline.payment_accepted` (grant, event time, receipt time, delay, gap). Contacts are frequent and are not audited one by one.

## Configuration (`Offline` section; the section is optional, every value is range-checked at start)

`Window` (72 h; 1 h to 7 d), `MinGap` (10 min; at least twice `ContactInterval`), `ClockSkew` (2 min; up to 10 min), `ContactInterval` (5 min), `GrantMaxAge` (30 d; at least `Window`), `EvidenceRetention` (90 d). `Hardening:OfflinePermitsPerMinute` (60).

## Known limits

- The server cannot distinguish a deliberately silent user from a disconnected one (above).
- A payment taken without a grant keeps the existing rule (a date no later than now and not before the sale); that online rule is not changed here.
- Several devices of one user: issuing on one supersedes the other; shared logins across devices are not supported by this design.
- The grant and contact endpoints are rate limited per client address; behind a reverse proxy the forwarded-header handling of DEPLOY-002 applies.
