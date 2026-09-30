# Roadmap

Epics are grouped into phases. A usable first product exists at the end of Phase 2; later phases are not prerequisites for it.

## Phase 0 — Foundation

| Epic | Notes |
|---|---|
| EPIC 01 — Engineering Foundation | Solution, persistence, config, logging, test infra, CI |

## Phase 1 — Tenancy and Access

| Epic | Notes |
|---|---|
| EPIC 02 — Identity & Organizations | Organizations, authentication, authorization, tenant isolation |

## Phase 2 — Core Operations (first usable product)

The first vertical slice: Product → Purchase → Inventory → Sale → Inventory Update.

| Epic | Notes |
|---|---|
| EPIC 03 — Product Catalog | |
| EPIC 04 — Inventory | Movement-based stock |
| EPIC 05 — Purchasing | Purchase orders, receiving |
| EPIC 06 — Sales | Sales that decrement stock atomically |

A minimal Vue UI for these flows is part of each epic's backlog items (Web project is created in FOUNDATION-007).

## Phase 3 — Intelligence

| Epic | Notes |
|---|---|
| EPIC 09 — Business Intelligence | Stock forecast, reorder recommendation, owner approval |
| EPIC 10 — Notifications | Surfacing recommendations to the owner |

## Phase 4 — Broader Business Operations

| Epic | Notes |
|---|---|
| EPIC 07 — Customers | |
| EPIC 08 — Expenses | |
| EPIC 13 — Files & Receipts | |
| EPIC 14 — Reporting | |

## Phase 5 — Automation and AI

| Epic | Notes |
|---|---|
| EPIC 11 — Automation | Safe routine operations with execution history |
| EPIC 12 — AI Assistant | Natural-language access via controlled tools |

## Cross-cutting (progressive, start early, finish late)

| Epic | Notes |
|---|---|
| EPIC 16 — Security & Compliance | Baseline in Phase 1; hardening (rate limiting, audit review) before any external users |
| EPIC 17 — Observability | Logging in Phase 0; correlation IDs, health checks, audit events progressively |
| EPIC 18 — Deployment & Infrastructure | Docker for local dev early; production deployment requires human approval |

## Phase 6 — Commercialization

| Epic | Notes |
|---|---|
| EPIC 15 — SaaS Billing | After the product proves value; needs approval (paid external services) |

## Note on Customers

The first slice records sales without a customer requirement (walk-in sale). Customers (EPIC 07) attach to sales later without changing the slice.
