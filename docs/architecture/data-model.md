# Data Model

Conceptual model and conventions for the first vertical slice. Detailed schemas are defined in backlog items and migrations; this document sets the rules they follow.

## Conventions

- PostgreSQL via EF Core; **migrations only** for schema changes.
- Primary keys: UUID, created in application code.
- Every business-owned table has a non-null `OrganizationId` (FK) and indexes that lead with it. Uniqueness is scoped per organization, e.g. products by SKU. A SKU is compared case-insensitively while its entered casing is preserved ([ADR 0006](../decisions/0006-sku-uniqueness.md)); the database must enforce this, not only the handlers.
- Timestamps in UTC (`timestamptz`): `CreatedAtUtc`, `UpdatedAtUtc`; actor columns where auditability matters.
- Money: `numeric(19,4)` plus currency code. Quantities: `numeric(18,4)`. No floating point.
- Tables and columns use EF Core's default names (PascalCase, plural table names such as `Organizations`); no naming-convention package.
- Soft-deactivation instead of deletion for entities with history.
- Concurrency token on stock levels and on documents that change state (see [ADR 0004](../decisions/0004-inventory-concurrency.md)).
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

## Organizations Table

`Organizations(Id uuid PK, Name varchar(200), Currency varchar(3), TimeZoneId varchar(100), CreatedAtUtc timestamptz, UpdatedAtUtc timestamptz)`, all columns required. Identifiers are generated in application code. The Domain enforces the invariants; CHECK constraints are a backstop for data written by anything else: the name is not blank, the currency is exactly three upper-case letters (`^[A-Z]{3}$`), the time zone is not blank. The currency is stored as the code and read back through `Currency.Create`, so a code removed from the supported snapshot would stop loading; the snapshot is only changed deliberately.

## Inventory Integrity Model

- `InventoryMovement(Id, OrganizationId, ProductId, QuantityDelta, Reason, SourceType, SourceId, ActorId, OccurredAtUtc)`.
- Reasons: `PurchaseReceived`, `SaleCompleted`, `ManualAdjustment`, `Return`, `Damage`. `Transfer` is not modeled until stock locations exist. Source types: `PurchaseReceipt`, `Sale`, `Adjustment`. Manual adjustments require a note. The delta is signed, non-zero, with at most 4 decimal places and a magnitude that fits `numeric(18,4)`.
- The reason fixes the sign of the delta, and the Domain rejects a mismatch (`inventory_movement.delta_must_be_positive` / `inventory_movement.delta_must_be_negative`):

  | Reason | Allowed delta |
  |---|---|
  | `PurchaseReceived` | positive only |
  | `SaleCompleted` | negative only |
  | `ManualAdjustment` | positive or negative |
  | `Return` | positive only |
  | `Damage` | negative only |

  `QuantityDelta` itself stays a generic signed quantity; the meaning lives in `InventoryMovement`. A new reason must define its sign rule when it is added.
- `StockLevel` is one row per `(OrganizationId, ProductId)`; the Domain entity has no separate id and no concurrency token (the token is mapped by persistence, ADR 0004). Its quantity changes only through `StockLevel.Apply(movement)`, which rejects a movement for another product or organization and one that would make stock negative, leaving the level unchanged.
- `StockLevel.Quantity` is updated in the same transaction as its movement. A test/reconciliation query must be able to prove `StockLevel = SUM(movements)`.
- Single stock location per organization in the MVP; locations later add a `LocationId` dimension by migration.

## Multi-Tenancy Enforcement

- EF Core global query filter on `OrganizationId` for tenant-owned entities, sourced from the authenticated context, never from request bodies.
- Commands verify that referenced ids (e.g. `ProductId` on a sale line) belong to the caller's organization.
- Integration tests must prove that cross-tenant reads and writes fail.

## Migrations

Expectations: [development.md](../engineering/development.md#database-migrations).
