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

## Implementation (INVENTORY-002)

- **Token:** `StockLevel` has a shadow property `xmin` (type `xid`, `IsConcurrencyToken`) mapped to PostgreSQL's **system column** of that name; the Domain entity is unchanged. `xmin` exists on every table already, so **no migration operation creates it**: the `AddInventory` migration has no `xmin` column (the Npgsql scaffold writes one and its SQL generator ignores it; it is removed from the migration so the code says what the SQL does), while the model snapshot keeps the property so EF uses it as the token. EF puts it in the `WHERE` of every update of a stock level. Proven by `FreshMigrationTests`, which migrates a database created empty by the test: no migration operation creates an `xmin` column, `pg_attribute` shows only the system `xmin` (negative column number; `information_schema` never lists system columns, so it cannot show this), EF's value equals PostgreSQL's, the `UPDATE` filters on it, and a second writer is refused.
- **Where the logic lives:** `StockMovementRecorder` (Application) is the internal service. `StageAsync` adds one movement and the stock change to the current unit of work without saving, so a use case with several lines stages all of them and saves once. **A refused movement tracks nothing:** the first stock level of a product is created in memory and added to the unit of work only after the movement was applied, so an insufficient-stock refusal leaves no pending level or movement and the other staged lines are undisturbed. `RecordAsync` stages and saves one movement with the retry. `ExecuteWithRetryAsync` is the retry for a caller that saves itself. The callers (Purchasing, Sales, adjustments) check their own permission first; the recorder only refuses to act without an organization.
- **Retry:** at most `MaxAttempts` = 3, with a short random pause. **It retries stock conflicts only:** a `DbUpdateConcurrencyException` involving a `StockLevel`, or the loss of the race to open a product's first stock level (an internal `StockLevelRaceException`). A concurrency failure of any other entity in the same unit of work is rethrown untouched, never retried and never reported as `inventory.concurrency_conflict`. Each attempt starts with a cleared change tracker (`IAppDbContext.ClearChangeTracker`, a small addition to the port) and **re-runs validation**, so stock that ran out meanwhile is returned as `stock_level.insufficient_stock` (a rule violation, 422), not as a conflict. A conflict that outlasts the attempts is `inventory.concurrency_conflict` (409).
- **Idempotency:** the key is `(OrganizationId, SourceType, SourceId, ProductId, Reason)`. A repeat is detected first and returns `AlreadyRecorded` without changing anything; if a concurrent request wins the race, the unique index `IX_InventoryMovements_Idempotency` refuses the second and the recorder, after looking at what the database now holds, returns `AlreadyRecorded` too. The same recognition handles two requests opening a product's first stock level (the loser retries). Any other save failure is rethrown, never reported as one of these.
- **Append-only:** `AppDbContext` refuses to save a changed or deleted movement (`AppendOnlyViolationException`), with or without an organization. **This differs from the wording above** ("an EF `SaveChanges` interceptor"): a check inside the context cannot be left unregistered, so no test or host can forget it. It guards the application's writes; a raw SQL statement bypasses it like any EF guard (a database trigger was not added).
- **Database backstops:** `CHECK ("Quantity" >= 0)`, non-zero delta, the reason/sign rule, a required note for a manual adjustment, known reason and source values, the unique idempotency index, and a **composite foreign key** `(ProductId, OrganizationId)` to the product (through a unique key on the product), so a movement or level cannot belong to a different organization than its product. There is deliberately no foreign key from a movement's actor to the user table (a movement outlives users; system actors are an open question).
- **Reconciliation:** the query of ADR 0004 is `InventoryPersistenceTests.ReconciliationSql`: every stock level whose quantity differs from the sum of its movements. It must return no rows; tests run it after every concurrent scenario and after a 60-step mixed sequence.
- **Proven:** parallel sales of the last unit leave exactly one success; parallel sales never take more than exists; receipts and sales in parallel reconcile; the retry re-validates; a conflict that never clears is returned after three attempts and saves nothing; the races are also forced deterministically with a save interceptor.

## When to reconsider (move to B or C for a specific path)

Measured evidence, not speculation: e.g. conflict/retry rate consistently above a few percent on hot products, bulk imports or POS bursts against the same SKUs, or 409s reaching users in normal operation. Then adopt option C (atomic conditional update) for that path, or B with a fixed lock order, via a new ADR. The movement ledger, idempotency constraint and CHECK backstop remain unchanged in either case.

## Consequences

- Simple, provider-native, testable implementation; the domain stays free of locking code.
- Clients must be ready for 409 on rare conflicts.
- Hot-row contention is a known limit, documented and monitored (log conflict counts; observability epic).
