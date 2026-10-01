# 0006 — SKU uniqueness

- Status: **Accepted** (decided by the project owner on 2026-10-01 during review of the CATALOG-001 pull request)
- Date: 2026-10-01
- Deciders: project owner

## Context

A product's SKU must be unique within an organization (modules.md). The documents said `(OrganizationId, Sku)` without stating whether `ABC-123` and `abc-123` are the same SKU, and a unique index on the raw column would treat them as different. The `Product` entity trims the SKU and preserves its casing. Uniqueness is a rule across products, which one entity cannot see, so it cannot be enforced in the Domain.

## Decision

1. **Scope:** a SKU is unique within one organization. The same SKU may exist in different organizations.
2. **Case-insensitive:** `ABC-123` and `abc-123` are the same SKU for uniqueness.
3. **Casing is preserved:** the stored and displayed SKU is what the user entered, after trimming. It is not lowercased, uppercased or otherwise normalized to achieve uniqueness.
4. **Domain:** `Product` trims the SKU, preserves its casing and enforces its length. It does not enforce uniqueness and carries no normalized copy of the SKU.
5. **Enforcement belongs to the persistence and application boundary** (CATALOG-002 and CATALOG-003). The database must enforce uniqueness with a case-insensitive strategy (for example a unique expression index on `(OrganizationId, lower(Sku))`, or a case-insensitive column type). The handler check exists to return a friendly conflict error and is not sufficient on its own, because concurrent requests can race past it. The database and the handler must use the same comparison semantics.
6. **Lookups by SKU** are case-insensitive as well.

## Not decided here

Whether a deactivated product keeps its SKU reserved. The rule as documented has no exception for deactivated products (products are never deleted), so CATALOG-002 must not add one without a decision.

## Consequences

- The Domain model is unchanged. This record, `data-model.md`, `modules.md` and the CATALOG-002/003 backlog items state the requirement so the persistence work cannot silently implement case-sensitive uniqueness.
- CATALOG-002 must prove the rule with integration tests against real PostgreSQL (FOUNDATION-008), including case variants within an organization and the same SKU in two organizations.
- Nothing enforces this rule yet: persistence does not exist.

## Alternatives considered

- **Lowercase or uppercase the stored SKU:** simple uniqueness, but it destroys the casing the owner entered and wants displayed. Rejected.
- **Case-sensitive uniqueness:** allows near-duplicate SKUs that differ only by case. Rejected.
- **Application-level check only:** racy under concurrent requests. Rejected.
