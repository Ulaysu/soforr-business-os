# 0007 — Connectivity resilience (offline-capable critical workflows)

- Status: Proposed
- Date: 2026-10-08
- Deciders: project owner

> **What is approved and what is not.** The owner's instruction of 2026-10-08 is an explicit **product decision**: offline reliability is part of Sellable V1, the server stays authoritative, and the offline layer reuses the existing commands. That decision is recorded here and in [sellable-v1.md](../product/sellable-v1.md). The owner also answered four design questions (sections 5, 6, 7 and 8). Everything else in this record is **a proposed implementation detail** and stays `Proposed` until the owner sets `Accepted` (an agent never does, see [README](README.md)). Work that needs no browser code (OFFLINE-001) may start once this ADR is accepted.

## Context

Soforr V1 was defined as connected-first, with offline synchronization deferred. The first target markets lose connectivity often enough that a business that cannot sell during an outage will not trust the product. The decision is global, not regional: any business with intermittent connectivity (a warehouse with poor Wi-Fi, a rural shop) benefits.

The honest V1 promise is: **critical business operations continue offline; cloud synchronization resumes automatically when connectivity returns.** It is not "the whole Business OS works offline".

### What the repository already gives us (inspection, 2026-10-08)

| Operation | Client-chosen id | Replay of the same request | Conflict answers |
|---|---|---|---|
| `CreateSale` | `saleId` | 200 `alreadyRecorded` when products, quantities, prices, reasons, customer and amount paid match; 409 `sale.id_conflict` otherwise | 422 insufficient stock; 404 product/customer; 403 permission |
| `RecordPayment` | `paymentId` | same payment: 200; different: 409 | 422 over-payment; 404 sale |
| `ReversePayment` | `reversalId` | same: 200; different: 409 | |
| `ReceivePurchase` | `receiptId` | same: 200; different: 409 | |
| `AdjustInventory` | `adjustmentId` | same: 200; different: 409 | |
| `VoidSale` | none | **not replay-safe** (a second void is `sale.not_voidable`) | |
| `CreateCustomer`, `CreateProduct`, `CreateSupplier`, `CreatePurchaseOrder` | none | **not replay-safe** (a retry creates a duplicate) | |

Other facts the design depends on:

- The idempotency mechanism is *the client-chosen entity id, scoped to the organization*, compared strictly on repeat, with refused requests reserving nothing ([ADR 0004](0004-inventory-concurrency.md)). The offline layer reuses it; it does not add a second one.
- A sale commits the sale, its stock movements and its audit events in **one transaction**; insufficient authoritative stock refuses the whole sale. Stock is never overwritten.
- Payments and movements are append-only. Tenant isolation is server-side (query filters, write guard); the organization comes from the session, never from a request.
- **The server stamps a sale with its own clock** (`CreatedAtUtc`, and the credit due date derived from it). Replayed after an outage, a sale would be dated at sync time. Payments already accept a client `paidAtUtc`, bounded by the recording time.
- **The server reads the catalog price when the sale is recorded.** Replayed after a price change, a sale would silently be charged a price the customer never saw. A stated `unitPrice` is a controlled override (`SalesOverridePrice`, SALES-004) and is not a way to carry "the price shown".
- Session: HttpOnly cookie `__Host-soforr.session`, idle timeout 1 hour, absolute lifetime 12 hours, security stamp and membership re-validated every minute ([ADR 0002](0002-authentication.md)). Unsafe requests need an antiforgery token bound to the identity, fetched from `GET /api/v1/auth/antiforgery`. A sale is attributed to the authenticated user.
- **`src/Soforr.Web` does not exist yet** (FOUNDATION-007 is Ready, no screens). There is no frontend state management, API client, service worker or browser test tooling to adapt. The browser part of this decision is therefore built on top of the scaffold and the online screens; it cannot start before them.

## Decision

### 1. Architecture: the browser holds an operational copy, not the truth

```text
UI (Vue)
  ↓
Client application service ("record sale", "record payment")
  ↓
Local repository (IndexedDB)  ──►  Outbox (SyncOperation records)
                                        ↓
                                   Sync engine ──► API client ──► existing API ──► existing commands ──► PostgreSQL
```

- The server and PostgreSQL remain the only source of truth. The local store is an **operational cache plus an outbox**, bounded and disposable except for unsynchronized operations.
- Modular monolith, one backend, no microservices, no message broker, no CRDTs, no event sourcing, no WebSockets for sync, no second source of truth.
- Vue components talk only to the client application services. The sync engine owns the queue, retries, backoff, state, acknowledgement and failure classification; components never touch IndexedDB internals.
- **The client queues commands, not domain logic.** An offline sale is the `CreateSale` request the online path would have sent, persisted and replayed. The client may validate for usability (a quantity is positive); it never re-implements stock, credit, payment or pricing rules.

### 2. Classification

| Offline-capable (V1) | Online-required |
|---|---|
| Product lookup and search from the cached catalog (including barcode) | Organization settings, administration |
| Customer lookup from the cached customers | User invitations, role/permission changes |
| Sales, including credit sales to cached customers | E-mail verification, password reset, sign-in |
| Recording payments against synced sales | Creating products, customers, suppliers (not replay-safe today) |
| Local history of recent operations, the sync queue and its status | Purchasing, receiving, stock adjustments, voids and payment reversals (need current authority) |
| Cached insights **with a freshness timestamp** (read-only) | Insight recalculation, reporting, anything needing server-wide fresh data |

Voiding, reversing and receiving are online-required because they correct or add to state that needs a current server view and, for void, are not replay-safe. They can be reconsidered later.

### 3. Local persistence

- **IndexedDB**, through the smallest mature wrapper the scaffold justifies (decided in OFFLINE-002; a dependency needs approval). Never `localStorage`, `sessionStorage` or memory for business data. `navigator.storage.persist()` is requested and storage pressure is surfaced.
- One database **per user and organization** (`soforr:{organizationId}:{userId}`), so another organization's or user's data cannot be reached by construction. On sign-in the stored identity must equal the server-confirmed identity, otherwise the store is not opened.
- Stores: `products`, `customers`, `sales` (with lines), `payments`, `outbox`, `meta` (identity, last sync times, schema version, last online validation).
- **Bounds (V1 defaults, configurable):** all active products up to 5,000 (more is refused with a clear message), active customers up to 2,000, the user's sales of the last 14 days or 200, whichever is fewer, pending operations unbounded but a visible warning at 500. Cost prices are cached only for a user who holds `ProductViewCost`. The cache is for operating the business, not a copy of PostgreSQL; history and intelligence stay on the server.
- Reference data (products, customers): the server snapshot replaces the local copy on each successful refresh (full or "changed since", decided in OFFLINE-001); no merge. Each record carries `fetchedAtUtc`; the UI shows how old the data is.

### 4. The outbox

```text
SyncOperation
  operationId   UUID v7, generated on the client (equals the entity id: saleId / paymentId)
  schemaVersion integer, the version of the payload contract
  type          'sale.create' | 'payment.record'
  userId, organizationId   who and where it was recorded (a local guard; never sent as authority)
  payload       the exact request body
  dependsOn     [operationId]   (a payment depends on its sale)
  occurredAtUtc when it happened on the device
  status        pending | sending | synced | retrying | conflict | error | blocked
  attemptCount, lastAttemptAtUtc, nextAttemptAtUtc
  lastError     { httpStatus, code, message }   (server codes only; no personal data)
```

- **Durability rule:** the local transaction and its outbox record are written in **one IndexedDB transaction**, and the UI says "Saved on this device" only after it commits. Nothing is reported saved from memory.
- **Idempotency is the existing one.** `operationId` is the sale id or payment id sent to the API, so every retry of the same operation carries the same identity. The server answers a repeat with 200 `alreadyRecorded`, which the client treats as success. A lost response after a server commit is therefore harmless.
- **Ordering.** The engine processes operations sequentially in `occurredAtUtc` order (then id), but an operation waits for its `dependsOn`. A payment recorded for an unsynced sale waits for it; a conflicting sale blocks its payments (`blocked`) and never blocks unrelated sales.
- A synced operation is kept locally for the history window and then removed; an operation in `conflict`, `error` or `blocked` is **never deleted automatically**.

### 5. Event time of an offline sale (owner decision: client time, bounded)

`CreateSale` gains an optional `occurredAtUtc`. The server accepts it only when it is not in the future (a small clock-skew allowance) and not older than the **offline window**; otherwise the request is refused with a validation error. The sale is dated at `occurredAtUtc` (so its due date, margin period and insight periods follow the real sale), and the server records its own **received time** separately. Without the field the server uses its clock, as today. The server's received time and the gap between the two are part of the audit trail and of sync metrics. Payments keep their existing `paidAtUtc`.

### 6. Price drift (owner decision: conflict, needs attention)

Each offline sale line also states the catalog price the cashier saw (`expectedCatalogPrice`). If the server's catalog price differs, the sale is **not** recorded: the server answers 409 `sale.catalog_price_changed`, the operation becomes `conflict`, and a user with `SalesOverridePrice` chooses to re-submit with the price the customer was shown (an override with a reason, audited) or the current price. Nothing is silently charged differently. A stated `unitPrice` keeps its existing meaning and permission.

### 7. Authentication and the offline security boundary (owner decision: 72 hours)

ADR 0002 stands: the cookie is the only credential, nothing credential-like is stored in the browser, passwords are never stored.

- **Eligibility.** Offline operation exists only on a device where the user signed in online and the app has validated the session online. The app records `lastOnlineValidationAtUtc` (not a secret).
- **Bounded window.** Local use continues for **72 hours** after the last successful online validation (configurable, bounded by a maximum). After that the app **locks local access**; the pending operations stay stored and are synced after the user signs in again. There is no indefinite offline bypass and no offline credential.
- **Same user only.** Outbox operations are bound to their user and organization. They sync only in a session of that same user and organization; a different user signing in on the device cannot send, see or discard them. The sale is attributed by the server to the authenticated user, so this also keeps attribution truthful.
- **Server re-checks everything on replay.** Permission, tenant, product/customer state and stock are evaluated at sync time with the user's *current* membership. A role change or deactivation while offline makes the operation fail (403/401) into `conflict`/`error`, not into a bypass.
- **Antiforgery.** The sync engine fetches a fresh antiforgery token before sending. An expired session (401) pauses syncing with "sign in to continue"; the operations wait.
- **Sign-out (owner decision).** Sign-out is **blocked while operations are pending**; an explicit "sign out anyway" keeps the pending operations on the device, locked to that user and organization. With nothing pending, sign-out clears the local store.
- **Data at rest.** IndexedDB is not encrypted. The cache is therefore limited to what the user may see anyway (no cost prices without `ProductViewCost`, no passwords, no tokens, minimal customer contact details: name and credit terms only). A lost or shared device is a recognised residual risk, mitigated by the bounded window, clear-on-sign-out and the rule above; local encryption is deferred and would need its own review.

### 8. Conflict policy (per entity, no generic last-write-wins)

| Entity | Policy |
|---|---|
| Sale | Append-style: accept once, idempotent replay. A refusal (insufficient stock, price changed, product/customer inactive or gone, forbidden) becomes `conflict`, shown with the server's reason. The physical goods may already have left, so resolution is an explicit user action: fix the cause (receive stock, adjust, change price) and **retry**, or an authorized user **discards with a reason** (kept in the local history as discarded, never silent). |
| Payment | Append-only, idempotent replay. Over-payment or an unknown sale becomes `conflict`. A payment is never merged or overwritten. |
| Inventory | Server decides. The UI may show an *estimated* stock (cached level minus pending local sales), labelled as such; it is never sent as a quantity. |
| Products, customers | The server snapshot replaces the local copy. |

**Failure classification.** Retryable: no network, timeout, 502/503/504, other 5xx (bounded). Terminal: 400 validation, 401 (pause: sign in), 403, 404, 409 conflict, 422 business rule. Exponential backoff from 2 s doubling to 5 minutes with full jitter; network failures never exhaust the queue (they retry indefinitely at the slow rate and show "waiting for connection"); other retryable failures stop after 8 attempts into `error` with a manual retry. Triggers besides the timer: application start, the `online` event and a successful reachability check, the page becoming visible, and an explicit "Sync now". Background Sync is only an optimization.

### 9. Connectivity state

`navigator.onLine` is a hint only. The app confirms reachability with a cheap unauthenticated request to `/health/live` (never limited by SECURITY-002). States: `online`, `offline`, `syncing`, `synced`, `pending`, `error`, `conflict`; the UI shows one calm line ("Offline — changes are being saved on this device", "Syncing 4 changes…", "All changes synchronized", "1 change needs attention") and, per sale, "Saved on this device / Waiting for synchronization / Synchronized".

### 10. Service worker

A service worker caches the **application shell and static assets only**, so the app can open without a network. It does not cache API responses and holds no business data. A new worker version **waits** until the user reloads and no sync is in flight, and an update never touches IndexedDB. The service worker, IndexedDB, the outbox and the sync engine are separate concerns by design.

### 11. Versioning

Every operation carries `schemaVersion`; the local database has numbered migrations that run on open and never drop the outbox. After a deployment, an operation the server can no longer accept (a contract change) is marked `blocked` with "this change was recorded by an older version and needs attention", never dropped. The API stays `/api/v1` compatible for the replayed commands for at least the offline window plus margin; breaking those commands needs a new ADR.

### 12. Audit and intelligence

The authoritative audit event is written by the server when the command commits; the client never fabricates one. Offline status in the UI is "pending" until the server acknowledges. Intelligence stays derived from server data: offline it is shown from the last sync with its timestamp and a "not including changes waiting on this device" note, never recomputed from partial local data.

### 13. Observability

Client: pending count, oldest pending age, attempts, successes, failures, conflicts, retry counts, sync latency (local counters surfaced in a diagnostics view and reported to the server in a tiny, non-personal heartbeat, decided in OFFLINE-006). Server: a structured log and metric for every operation that arrives late (age = received − occurred), plus the existing correlation ids. Nothing logged contains passwords, tokens, payloads or customer details.

## Staged implementation

| Item | What |
|---|---|
| OFFLINE-001 | Replay-safe server contract: `occurredAtUtc`, expected catalog price, received time, error taxonomy, cache feeds. **Backend; can start first.** |
| OFFLINE-002 | Client persistence, outbox, sync engine and connectivity foundation (after FOUNDATION-007) |
| OFFLINE-003 | Offline product and customer cache, service-worker shell |
| OFFLINE-004 | Offline sales (needs SALES-003) |
| OFFLINE-005 | Offline payments and credit sales |
| OFFLINE-006 | Sync reliability, conflict UX, observability |
| OFFLINE-007 | End-to-end verification in a real browser |

## Consequences

- Positive: businesses keep selling through outages; the server's guarantees (idempotency, atomic stock, append-only money, tenant isolation, audit) are reused, not duplicated.
- The client grows a real subsystem (store, outbox, engine) that must be tested as seriously as the server; browser tests (a real offline browser) are required, and a browser-automation dependency needs the owner's approval (OFFLINE-007).
- Two small server contract changes (event time, expected catalog price) touch sales; both are additive and optional.
- Offline conflicts can concern goods that already left the shop. The product accepts that and makes resolution explicit instead of hiding it.
- Estimated stock offline can be wrong; it is labelled, never authoritative.
- The offline window, caps and retry numbers are defaults to be tuned with the first customer.

## Deferred

Offline creation of customers/products (needs replay-safe create), offline void/reversal/receiving, Background Sync as a primary mechanism, local encryption, multi-device live stock reservation, multi-location sync, push-based sync.

## Alternatives considered

- **CRDTs / event sourcing / local-first database with automatic merge:** unsafe for money and stock (merge would manufacture stock or lose payments) and a large new subsystem. Rejected.
- **Last-write-wins on stock or sales:** rejected by the owner and by ADR 0004.
- **Long-lived offline token or stored credentials:** a permanent bypass and a theft target. Rejected.
- **Server-time dating of offline sales:** unforgeable but wrong for due dates and periods. Rejected by the owner.
- **Charging the server's current price on replay:** silent change of what the customer paid. Rejected by the owner.
- **Native app:** out of V1 scope.
