# 0007 — Connectivity resilience (offline-capable critical workflows)

- Status: Accepted
- Date: 2026-10-08
- Deciders: project owner

> **Status.** Accepted by the project owner on 2026-10-08. The offline authorization mechanism it left open is decided in [ADR 0008](0008-offline-authorization-boundary.md) and implemented by OFFLINE-001; the contract is in [offline-sync-contract.md](../engineering/offline-sync-contract.md). The items below that name later work items are still open.

### Approved product decisions (owner, 2026-10-08)

These are decided and are not reopened by the implementation work:

1. Offline reliability is part of Sellable V1: V1 is connectivity-resilient; critical workflows continue during temporary connectivity loss. The claim is never "the whole Business OS works offline".
2. The server and PostgreSQL remain authoritative; the browser holds an operational copy and a durable outbox.
3. The existing commands and their idempotency (client-chosen ids) are reused; no second idempotency system.
4. Offline operation is allowed for up to **72 hours** after a successful online validation, configurable within safe bounds.
5. An offline sale's business time is the **client event time**, bounded by a server-established offline authorization (section 5); the server separately records its receipt/synchronization time.
6. If the normal catalog price changed while a device was offline, synchronization produces a **conflict that needs attention**; a sale is never silently re-priced (section 6).
7. **Sign-out is blocked while operations are pending**, with an explicit "sign out anyway" override (section 7).
8. Pending operations stay bound to their original user and organization.
9. **Playwright is approved** as the browser automation dependency for OFFLINE-007. It is not installed by this ADR or by any documentation-only change; it is introduced when the web and end-to-end infrastructure is built.
10. OFFLINE-001 is the next backend task after acceptance.

### Implementation decisions that belong to the work items, not to this ADR

Not finalized here, and not to be treated as decided: the exact **offline authorization mechanism** and its contract (decided in ADR 0008, implemented in OFFLINE-001); the exact cache-feed API (OFFLINE-001); the IndexedDB wrapper or none (OFFLINE-002); the exact sync algorithm, state-management and schema/version-migration implementation (OFFLINE-002); the conflict UI (OFFLINE-006); the telemetry implementation (OFFLINE-006). Numbers in this record (cache bounds, backoff, attempt counts) are proposed defaults.

### Principle: timestamp age is not authorization

A timestamp that falls inside the offline window proves nothing: any client can send one. The server accepts an offline event time only for an operation covered by an **offline authorization boundary that the server itself established** at a successful online validation (section 5). Without that evidence a business time other than the server's own is refused, never trusted.

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
- One database **per user and organization** (`soforr:{organizationId}:{userId}`) as a logical guard against accidental mixing (not a security boundary, see section 7). The application opens a store only after the server-confirmed identity and organization equal the ones recorded in it.
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
- **Idempotency is the existing one.** `operationId` is the sale id or payment id sent to the API, so every retry of the same operation carries the same identity. The server answers a repeat with 200 `alreadyRecorded`, which the client treats as success. This is the core reliability property, and it is exactly the existing behaviour:

  ```text
  client sends the operation → server commits → the response is lost
  → client retries the same operation id → server recognizes the existing operation
  → server returns already-recorded (the same result) → client marks the operation synchronized
  ```

  For offline V1 **only operations that are already safely replayable are replayed** (`CreateSale`, `RecordPayment`). No generic idempotency framework is introduced unless OFFLINE-001 proves one is technically necessary. The other identified commands (`ReversePayment`, `ReceivePurchase`, `AdjustInventory`) already have ids but remain online-required.
- **Ordering.** The engine processes operations sequentially in `occurredAtUtc` order (then id), but an operation waits for its `dependsOn`. A payment recorded for an unsynced sale waits for it; a conflicting sale blocks its payments (`blocked`) and never blocks unrelated sales.
- A synced operation is kept locally for the history window and then removed; an operation in `conflict`, `error` or `blocked` is **never deleted automatically**.

### 5. Event time of an offline sale, and the offline authorization boundary

**Decision (owner): client time, bounded by a server-established authorization.** `CreateSale` gains an optional `occurredAtUtc`. The sale (and so its credit due date, margin period and insight periods) is dated at that event time, and the server records its own **received time** separately; the gap is part of the audit trail and of sync metrics. Payments keep their existing `paidAtUtc`.

**The boundary.** The server accepts `occurredAtUtc` only for an operation covered by an **offline authorization boundary originating from a successful online validation**, and the operation's event time must lie inside that boundary. The server must be able to tell apart:

- a legitimate offline operation, created inside an authorized offline window, from
- an arbitrary request trying to backdate a transaction (for example a sale made today at 14:00 sent with an event time of yesterday 10:00, which is inside any 72-hour window).

Being "not older than 72 hours" is therefore necessary but never sufficient. The 72 hours are the product policy (configurable within safe bounds); *how* the server establishes and later validates the boundary is **an OFFLINE-001 design decision**. This ADR deliberately does not choose a token, signature, device credential or encryption scheme. Repository inspection found no existing offline-grant mechanism to reuse; the nearest existing facts are the session ticket (which already carries the sign-in time and is re-validated against the security stamp and membership every minute, ADR 0002) and the antiforgery token (bound to an identity). OFFLINE-001 must define the **minimum secure contract** to establish and validate the boundary, say what it does and does not protect against (it must not add a long-lived bearer secret that outlives the policy, ADR 0002), and prove it with tests, including the backdating attack above.

**Fail safely.** An event time outside the authorized boundary, or an offline operation with no valid boundary, is refused with an explicit error. The server **never silently falls back to server time**: re-dating an offline sale as if it happened at sync time would corrupt due dates, margins and history. The operation becomes a conflict/error the user can see. A request without `occurredAtUtc` is an ordinary online sale at server time, as today.

### 6. Catalog price drift, overrides and negotiated prices

**Decision (owner): conflict, needs attention.** Three different things must never be confused:

1. **The normal catalog price**, which can change while a device is offline.
2. **An authorized price override** (SALES-004: a stated final `unitPrice` with a reason, needing `SalesOverridePrice`; below cost needs `SalesSellBelowCost`). Its meaning and permissions are unchanged by this ADR.
3. **A negotiated or customer-specific final price**, which in V1 is simply an authorized override (there are no price lists).

An offline sale line therefore preserves the price context the cashier actually saw: the **catalog price shown** (so the server can detect that the catalog moved) and, when the cashier used an override, the **final price and reason as today**. Catalog drift is decided only by comparing the catalog price the cashier saw with the current authoritative catalog price. A legitimate override or negotiated price is **not** drift merely because its final price differs from the catalog, and drift is never inferred from the final price.

When the normal catalog price changed, the sale is not recorded: the server answers 409 `sale.catalog_price_changed`, nothing is reserved, the operation becomes `conflict`, and a user with the right permission resolves it explicitly (accept the price the customer was shown as a reasoned, audited override, or re-submit at the current price). Nothing is silently charged differently. The exact wire shape, and how a sale that carries both an override and a changed catalog price is judged, are OFFLINE-001 decisions; the invariant is that the final price the customer was shown is never silently altered and that the SALES-004 semantics are not redesigned.

### 7. Authentication, the offline security boundary and local storage

ADR 0002 stands: the cookie is the only credential, nothing credential-like is stored in the browser, passwords are never stored. The security flow is:

```text
Successful online authentication / validation
        ↓
Server establishes the offline authorization boundary (mechanism: OFFLINE-001)
        ↓
The user may operate offline within the bounded window (72 hours, configurable)
        ↓
The client records operations with their event time, in the local outbox
        ↓
Connectivity returns
        ↓
The server validates, for every operation, on its own authority:
   authenticated user · organization · the offline authorization boundary · event time and age
   · permission · product and customer state · stock · price state · business rules
        ↓
Commit  /  conflict  /  retry
```

**The client is authoritative for none of these decisions.** Everything the client stores or sends about identity, organization, time or prices is a claim the server verifies or ignores.

- **Eligibility.** Offline operation exists only on a device where the user signed in online and the app validated the session online. The app records `lastOnlineValidationAtUtc` (not a secret) for its own UI; the server-side boundary of section 5 is what the server relies on.
- **Bounded window.** Local use continues for the policy window after the last successful online validation. After that the app **locks local access**; pending operations stay stored and sync after the user authenticates again. There is no indefinite offline bypass and no offline credential.
- **Logical isolation (a guard, not a security boundary).** Each local store is scoped to `organizationId + userId`, and the application verifies the currently authenticated server identity and organization before opening or using that store. This is application-level isolation that prevents accidental mixing; it is **not** cryptographic protection. The client-side `userId` and `organizationId` are local metadata and guards only and are **never** treated as authoritative claims: the server derives user and organization from the authenticated session (ADR 0002) and ignores any client-supplied value.
- **Synchronization authorization.** An operation created by user A must never be synchronized with user B's server session. The server remains authoritative for user identity, organization membership, permissions, tenant isolation, product/customer authorization, stock validation and the financial and business rules. Because the server attributes a sale to the authenticated user, a replay by the original user also keeps attribution truthful.
- **Server re-checks everything on replay**, with the user's *current* membership. A role change or deactivation while offline makes the operation fail (403/401) into `conflict`/`error`, not into a bypass.
- **Antiforgery.** The sync engine fetches a fresh antiforgery token before sending. An expired session (401) pauses syncing with "sign in to continue"; the operations wait.
- **Sign-out with pending operations (owner decision).**
  - *Normal sign-out:* if operations are pending, sign-out shows the pending count and offers "Sync now" or waiting; once everything is synchronized, sign-out proceeds normally and the local store is cleared.
  - *Explicit override:* if synchronization cannot complete, "Sign out anyway" requires explicit confirmation. The pending operations **remain stored**, bound to their original user and organization; they cannot be synchronized (or seen, edited or discarded) by another user; they can resume after the original user authenticates again, **subject to the same offline/security rules** (the override does not extend the window, and operations outside the authorization boundary still fail safely). Pending business operations are never deleted by signing out.
- **Data at rest.** IndexedDB provides application-level storage isolation, **not strong protection** against someone with access to the same browser or device profile. V1 does **not** promise encrypted local storage. No passwords, session cookies, authentication tokens or other credentials are stored in IndexedDB. The cache is limited to what the user may see anyway (no cost prices without `ProductViewCost`, minimal customer details: name and credit terms only). Exposure on a lost, shared or compromised device is a **recognized residual risk**, reduced by the bounded window, identity binding, the sign-out behaviour, server-side authorization on replay and the minimal cache. Local encryption is deferred and would need its own security review and decision.

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
| OFFLINE-001 | Replay-safe server contract: the offline authorization boundary and the event-time rule, the catalog price seen (distinct from overrides), received time, error taxonomy, cache feeds. **Backend; starts first, after acceptance.** |
| OFFLINE-002 | Client persistence, outbox, sync engine and connectivity foundation (after FOUNDATION-007) |
| OFFLINE-003 | Offline product and customer cache, service-worker shell |
| OFFLINE-004 | Offline sales (needs SALES-003) |
| OFFLINE-005 | Offline payments and credit sales |
| OFFLINE-006 | Sync reliability, conflict UX, observability |
| OFFLINE-007 | End-to-end verification in a real browser |

## Consequences

- Positive: businesses keep selling through outages; the server's guarantees (idempotency, atomic stock, append-only money, tenant isolation, audit) are reused, not duplicated.
- The client grows a real subsystem (store, outbox, engine) that must be tested as seriously as the server; real-browser tests are required, and Playwright is approved for them (OFFLINE-007; installed when the web and end-to-end infrastructure is built, not before).
- Two server contract changes touch sales (event time under an offline authorization boundary, the catalog price the cashier saw); both are additive and optional, and the authorization boundary is a security-relevant design that OFFLINE-001 must specify and test before anything depends on it.
- Offline conflicts can concern goods that already left the shop. The product accepts that and makes resolution explicit instead of hiding it.
- Estimated stock offline can be wrong; it is labelled, never authoritative.
- The offline window, caps and retry numbers are defaults to be tuned with the first customer.

## Deferred

Offline creation of customers/products (needs replay-safe create), offline void/reversal/receiving, Background Sync as a primary mechanism, local encryption, multi-device live stock reservation, multi-location sync, push-based sync.

## Alternatives considered

- **CRDTs / event sourcing / local-first database with automatic merge:** unsafe for money and stock (merge would manufacture stock or lose payments) and a large new subsystem. Rejected.
- **Last-write-wins on stock or sales:** rejected by the owner and by ADR 0004.
- **Long-lived offline token or stored credentials:** a permanent bypass and a theft target. Rejected. (A short-lived, narrowly scoped server-issued authorization for the offline window is not this; its form is OFFLINE-001's decision.)
- **Server-time dating of offline sales:** unforgeable but wrong for due dates and periods. Rejected by the owner.
- **Trusting any client timestamp inside the window:** a backdating hole; timestamp age is not authorization. Rejected.
- **Silently re-dating an invalid offline sale to server time:** corrupts business history. Rejected.
- **Charging the server's current price on replay:** silent change of what the customer paid. Rejected by the owner.
- **Native app:** out of V1 scope.
