# Data Model

Conceptual model and conventions for the first vertical slice. Detailed schemas are defined in backlog items and migrations; this document sets the rules they follow.

## Conventions

- PostgreSQL via EF Core; **migrations only** for schema changes.
- Primary keys: UUID, created in application code.
- Every business-owned table has a non-null `OrganizationId` (FK) and indexes that lead with it. Uniqueness is scoped per organization, e.g. `(OrganizationId, Sku)`.
- Timestamps in UTC (`timestamptz`): `CreatedAtUtc`, `UpdatedAtUtc`; actor columns where auditability matters.
- Money: `numeric(19,4)` plus currency code. Quantities: `numeric(18,4)`. No floating point.
- Soft-deactivation instead of deletion for entities with history.
- Concurrency token on stock levels and on documents that change state (see [ADR 0004](../decisions/0004-inventory-concurrency.md), proposed).
- Database constraints as a second line of defense (CHECK for non-negative amounts, FKs, unique indexes).
- Append-only audit events for important business actions.

## Core Entities (first slice)

```text
Organization ─┬─ Membership ── User
              ├─ Product
              ├─ Supplier
              ├─ PurchaseOrder ── PurchaseOrderLine ── Product
              ├─ InventoryMovement ── Product        (append-only)
              ├─ StockLevel ── Product               (one per product; = sum of movements)
              ├─ Sale ── SaleLine ── Product
              ├─ ReorderRecommendation ── Product
              └─ AuditEvent
```

## Inventory Integrity Model

- `InventoryMovement(Id, OrganizationId, ProductId, QuantityDelta, Reason, SourceType, SourceId, ActorId, OccurredAtUtc)`.
- Reasons: `PurchaseReceived`, `SaleCompleted`, `ManualAdjustment`, `Return`, `Damage`, `Transfer` (future). Manual adjustments require a note.
- `StockLevel.Quantity` is updated in the same transaction as its movement. A test/reconciliation query must be able to prove `StockLevel = SUM(movements)`.
- Single stock location per organization in the MVP; locations later add a `LocationId` dimension by migration.

## Multi-Tenancy Enforcement

- EF Core global query filter on `OrganizationId` for tenant-owned entities, sourced from the authenticated context, never from request bodies.
- Commands verify that referenced ids (e.g. `ProductId` on a sale line) belong to the caller's organization.
- Integration tests must prove that cross-tenant reads and writes fail.

## Migrations

Expectations: [development.md](../engineering/development.md#database-migrations).
