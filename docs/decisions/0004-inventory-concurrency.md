# 0004 — Inventory concurrency

- Status: **Accepted** (approved by the project owner, 2026-09-30)
- Date: 2026-09-30
- Deciders: project owner

## Context

Inventory is critical data (CLAUDE.md §9). Concurrent operations must not oversell, double-receive or leave stock and movements inconsistent. Model (see [data-model.md](../architecture/data-model.md)): append-only `InventoryMovement` rows plus a `StockLevel` row per product that always equals the sum of its movements and is updated in the same transaction.

Workflow characteristics of the target customer (SMB): a handful of concurrent cashiers/users per organization, few writers per product, but real risk on the "last unit" and on retried requests (double click, network retry).

## Options

| | A. Optimistic concurrency | B. Pessimistic row lock (`SELECT ... FOR UPDATE`) | C. Atomic conditional `UPDATE` |
|---|---|---|---|
| Mechanism | Version token on `StockLevel`; write fails if row changed since read | Lock stock rows for the transaction | `UPDATE ... SET qty = qty - @n WHERE qty >= @n` |
| Conflicts | Detected at save → retry | Serialised; waiters block | Loser gets 0 rows affected |
| Fits EF Core domain model | Yes, built in | Needs raw SQL / `FromSql`; lock spans handler | Bypasses tracked entities |
| Deadlock risk | None | Yes with multi-line sales unless rows locked in a fixed order | None |
| Under high contention | Retry storms on hot rows | Fine but slows | Best |
| Complexity | Low | Medium | Medium (special-cases the model) |

## Decision: A, with database backstops

Use **optimistic concurrency**. The default is right for this workflow; nothing in the first slice demonstrates a need for stronger locking.

### Concurrency token
- `StockLevel` has a concurrency token. Recommended: PostgreSQL's `xmin` system column via the Npgsql provider (no domain pollution, changes on every update); the exact mapping API is verified against the installed Npgsql version in INVENTORY-002. An explicit `Version` column is the fallback if `xmin` proves awkward.
- Other state-changing documents (purchase orders, sales) get tokens too where concurrent edits are plausible.

### Conflicting updates
Two operations read the same `StockLevel` and both try to write: the first `UPDATE ... WHERE id = @id AND token = @old` succeeds; the second affects 0 rows and EF raises `DbUpdateConcurrencyException`. The second transaction rolls back completely (movement included).

### Transaction boundaries
Per ADR 0003: one command = one `SaveChangesAsync` = one transaction. A sale with N lines loads N `StockLevel` rows, checks availability, adds N movements and decrements N levels, and saves once. A conflict on any row rolls back the entire sale. Default isolation (Read Committed) is sufficient because correctness comes from the token check, not from isolation level.

### Retry behavior
- Retries happen at the **command level**, never inside the domain: on `DbUpdateConcurrencyException` discard the change tracker/unit of work, reload, **re-run validation** (so a now-insufficient stock is reported as a business error, not a conflict), and retry.
- Bounded: up to 3 attempts with small randomized backoff; then return `409 Conflict` (`inventory.concurrency_conflict`) so the client can retry.
- Only commands that touch stock use the retry helper. Non-idempotent side effects (emails, external calls) must not occur inside the retried block.
- Retried requests are safe because of idempotency keys (below).

### Movement integrity
- `InventoryMovement` is append-only: no update/delete methods in the domain; an EF `SaveChanges` interceptor rejects `Modified`/`Deleted` states for movements; corrections are new movements with a reason.
- Every movement has a reason, actor, source reference and the same `OrganizationId` as its product.
- **Idempotency:** unique constraint on `(OrganizationId, SourceType, SourceId, ProductId, Reason)` so re-receiving the same receipt line or re-completing a sale cannot create duplicate movements.
- **Database backstops:** `CHECK (Quantity >= 0)` on `StockLevel` (unless the organization explicitly allows negative stock, which is a separate approved feature), plus foreign keys and non-zero delta check.
- **Reconciliation:** an integration test and an operational query prove `StockLevel.Quantity = SUM(movements.QuantityDelta)`.

### Tests required (INVENTORY-002)
Parallel sales of the last unit yield exactly one success; concurrent receipt and sale keep the reconciliation invariant; retry after conflict re-validates; duplicate receipt produces one movement; all against real PostgreSQL (ADR 0005).

## When to reconsider (move to B or C for a specific path)

Measured evidence, not speculation: e.g. conflict/retry rate consistently above a few percent on hot products, bulk imports or POS bursts against the same SKUs, or 409s reaching users in normal operation. Then adopt option C (atomic conditional update) for that path, or B with a fixed lock order, via a new ADR. The movement ledger, idempotency constraint and CHECK backstop remain unchanged in either case.

## Consequences

- Simple, provider-native, testable implementation; the domain stays free of locking code.
- Clients must be ready for 409 on rare conflicts.
- Hot-row contention is a known limit, documented and monitored (log conflict counts; observability epic).
