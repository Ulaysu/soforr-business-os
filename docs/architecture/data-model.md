# Data Model

Conceptual model and conventions for the first vertical slice. Detailed schemas are defined in backlog items and migrations; this document sets the rules they follow.

## Conventions

- PostgreSQL via EF Core; **migrations only** for schema changes.
- Primary keys: UUID, created in application code.
- Every business-owned table has a non-null `OrganizationId` (FK) and indexes that lead with it. Uniqueness is scoped per organization, e.g. products by SKU. A SKU is compared case-insensitively while its entered casing is preserved ([ADR 0006](../decisions/0006-sku-uniqueness.md)); the database must enforce this, not only the handlers.
- Timestamps in UTC (`timestamptz`): `CreatedAtUtc`, `UpdatedAtUtc`; actor columns where auditability matters.
- Money: `numeric(19,4)` plus currency code. `Money` enforces the same range (at most 999,999,999,999,999.9999 in magnitude, `Money.MaxAmount`), so every Money the Domain accepts can be stored. Quantities: `numeric(18,4)`. No floating point.
- Tables and columns use EF Core's default names (PascalCase, plural table names such as `Organizations`); no naming-convention package.
- Soft-deactivation instead of deletion for entities with history.
- Concurrency token on stock levels and on documents that change state (see [ADR 0004](../decisions/0004-inventory-concurrency.md)).
- Database constraints as a second line of defense (CHECK for non-negative amounts, FKs, unique indexes).
- Append-only audit events for important business actions.
- **Transaction lines snapshot product data.** A sale or purchase line stores the product's name, SKU, unit price or cost and currency at the time, never only a reference to the product's current values, so history (and the intelligence computed from it) does not change when a product is edited.
- **Sale price integrity.** A sale line stores both the catalog price at sale time and the final applied unit price. If the final price differs because of a discount or override, the line stores the reason and actor needed by the authorization/audit policy. Margin uses the final applied price and the sale-time cost snapshot.

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

## Identity Tables

- `Users`, `UserClaims`, `UserLogins`, `UserTokens`: ASP.NET Core Identity's schema (credential records), renamed without the `AspNet` prefix. Identity roles are not used.
- `Memberships(Id uuid PK, UserId uuid FK, OrganizationId uuid FK, Role varchar(20), CreatedAtUtc timestamptz)`: **unique index on `UserId`** (one organization per user, ADR 0002), an index on `OrganizationId`, `CHECK (Role IN ('Owner','Manager','Staff'))`, and `ON DELETE RESTRICT` to both the user and the organization.
- A user's e-mail address is stored as entered and compared through Identity's normalized form (a unique index on `NormalizedUserName`, which equals the e-mail address), so `A@x.com` and `a@x.com` are the same account.

## Products Table

`Products(Id uuid PK, OrganizationId uuid FK, Sku varchar(64), Barcode varchar(64) NULL, Name varchar(200), UnitOfMeasure varchar(32), SellingPrice numeric(19,4), SellingPriceCurrency varchar(3), CostPrice numeric(19,4), CostPriceCurrency varchar(3), ReorderPoint numeric(18,4) NULL, ReorderLeadTimeDays int NULL, IsActive bool, CreatedAtUtc, UpdatedAtUtc, SkuKey varchar(64) GENERATED lower(Sku))`. Identifiers are generated in application code.

- Prices and the reorder settings are stored in the product's own row (owned value objects); both reorder columns are null when no settings are configured.
- **Unique index `IX_Products_OrganizationId_SkuKey` on `(OrganizationId, SkuKey)`**: the case-insensitive SKU rule of [ADR 0006](../decisions/0006-sku-uniqueness.md), also the index for SKU lookups within an organization.
- **Optional primary barcode:** V1 stores one barcode/GTIN per product. It is unique within an organization and indexed for exact lookup. The entered value is preserved; barcode semantics are intentionally identifier-only in V1. Multiple barcodes, case/unit identifiers and 2D/GS1 application identifiers are later.
- No other index yet: search and listing indexes are added as the queries exist.
- CHECK constraints (second line of defense): SKU, name and unit not blank; prices and reorder point not negative; lead time not negative; currency codes are three capital letters and the two price currencies are equal. Barcode, when present, is non-blank and within the maximum length. That the prices are in the *organization's* currency is a handler rule (it needs the organization).
- `OrganizationId` is a foreign key (`ON DELETE RESTRICT`), and the table has the tenant query filter ([ADR 0002](../decisions/0002-authentication.md)).
- No concurrency token yet: no requirement has asked for one on products.

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

## Stock Tables (INVENTORY-002)

- `InventoryMovements(Id uuid PK, OrganizationId, ProductId, QuantityDelta numeric(18,4), Reason varchar(30), SourceType varchar(30), SourceId uuid, ActorId uuid, Note varchar(500) NULL, OccurredAtUtc timestamptz)`. Reason and source type are stored as the enum names. Append-only. Unique index `IX_InventoryMovements_Idempotency` on `(OrganizationId, SourceType, SourceId, ProductId, Reason)`; an index on `(OrganizationId, ProductId, OccurredAtUtc)` for a product's history.
- `StockLevels(OrganizationId, ProductId, Quantity numeric(18,4), UpdatedAtUtc)`, primary key `(OrganizationId, ProductId)`, with the `xmin` concurrency token (a system column, not stored by the table definition).
- CHECK constraints: quantity not negative; delta not zero; reason and source type known; the reason fixes the sign of the delta (a manual adjustment may be either); a manual adjustment has a non-blank note; a note is never blank.
- Both tables have a composite foreign key `(ProductId, OrganizationId)` to `Products(Id, OrganizationId)` (`ON DELETE RESTRICT`), so they cannot belong to a different organization than the product. The product table gained the unique key `AK_Products_Id_OrganizationId` for it.
- Both have the tenant query filter and are covered by the write guard. Reconciliation: `StockLevel.Quantity = SUM(QuantityDelta)` per product, checked by the query in the tests.

## Multi-Tenancy Enforcement

- EF Core global query filter on `OrganizationId` for tenant-owned entities (`Organization` by its id, `Membership` today), sourced from the authenticated context, never from request bodies. With no organization the filter matches nothing. A write to another organization's row is refused on save. A convention test fails when a tenant-owned entity has no filter.
- Commands verify that referenced ids (e.g. `ProductId` on a sale line) belong to the caller's organization.
- Integration tests must prove that cross-tenant reads and writes fail.

## Migrations

Expectations: [development.md](../engineering/development.md#database-migrations).
