# Product Scope

The product is sequenced toward a **sellable V1** that customers in any country can use and that is independently valuable without a marketplace ([vision.md](vision.md) is the canonical source for positioning and direction; [sellable-v1.md](sellable-v1.md) is the canonical definition of V1: what is in, what is deferred and the exit criteria). This document lists the modules and where each stands. Module boundaries are in [modules.md](../architecture/modules.md); work items and their priority classes are in [backlog.md](backlog.md).

## Global Product Boundary

In V1, "global" means the Business OS can be sold to customers in different countries and is regionally configurable. It does not mean a marketplace or trade network (see [what "global" means](vision.md#what-global-means)).

Soforr is designed for customers worldwide. V1 prioritizes small and medium-sized wholesalers and distributors, using a focused operating model—connected web, single location or single stock pool—while retaining basic retail and light-wholesale sales support. The core domain remains independent of any one country or region.

Global requirements that should influence the architecture include organization currency, time zone, locale-aware presentation, international addresses and phone numbers, configurable tax concepts, and provider-based payment/integration boundaries. These should be implemented when required by the relevant product slice rather than hard-coded as regional behavior.

Country, currency, time zone and locale are distinct concepts and must not be conflated; the architecture principles are in [architecture.md](../architecture/architecture.md#future-architecture-boundaries-marketplace-and-country-markets).

The product experience should compete on clarity, speed and deterministic operational intelligence. It is not positioned as an Africa-only POS, a regional payment product, or a full ERP.

## Priority Classes

| Class | Meaning |
|---|---|
| **SELLABLE-P0** | Required to make the first customer-ready product |
| **PRODUCT-P1** | Valuable, not required for the first sale |
| **FUTURE** | Long-term capability (the Intelligent Business OS horizon) |

## Modules

| Module | Purpose | Class |
|---|---|---|
| Identity & Access | Users, authentication, roles, permissions, inviting the organization's users | SELLABLE-P0 |
| Organizations | Tenants; membership; organization settings | SELLABLE-P0 |
| Audit | Append-only record of business-sensitive actions | SELLABLE-P0 |
| Products / Catalog | What the business sells and buys; SKU/barcode identification, prices and reorder settings | SELLABLE-P0 |
| Inventory | Stock levels derived from recorded movements | SELLABLE-P0 |
| Purchasing | Buying stock from suppliers; receiving goods | SELLABLE-P0 |
| Sales | Fast sales that keep working through a connectivity outage, barcode/SKU lookup, controlled price overrides/discounts, and their effect on stock | SELLABLE-P0 |
| Connectivity resilience | Offline-capable critical workflows: local persistence, a durable outbox and automatic idempotent synchronization (ADR 0007) | SELLABLE-P0 |
| Customers | Who the business sells to; credit sales, recorded payments and balances | SELLABLE-P0 |
| Intelligence (deterministic) | Rule-based, explainable insights from business data: inventory risk, sales comparisons, margins, purchasing cost, customer aging, and the "things that need your attention" view | SELLABLE-P0 |
| Production readiness | Deployment, configuration, observability, backup and recovery | SELLABLE-P0 |
| Expenses | Recording non-inventory business costs (and expense insights) | PRODUCT-P1 |
| Reporting beyond the V1 metrics | Further verified summaries | PRODUCT-P1 |
| Notifications | Telling the owner what needs attention outside the app | PRODUCT-P1 |
| Files / Receipts | Attachments such as receipts and invoices (general attachments only; invoice capture is an unresolved owner decision, see [Open decisions](sellable-v1.md#open-decisions-affecting-scope)) | PRODUCT-P1 |
| Stored, approvable reorder recommendations | Recommendations as records the owner approves, creating a purchase order | PRODUCT-P1 |
| Statistical intelligence | Forecasting, demand prediction, statistical anomaly detection | FUTURE |
| AI assistant | Natural-language access, generated explanations (over the deterministic services) | FUTURE |
| Automation | Safe, approved routine operations | FUTURE |
| Marketplace and commercial network | Opt-in public listings, business discovery, country markets, cross-border trade ([vision.md](vision.md#direction-from-business-os-to-a-commercial-network)); not designed, no date | FUTURE |

## The Core Slice

The workflow that defines V1. Its purpose is to prove the system can move from raw business activity to useful operational insight, and to be demonstrable end to end.

```text
Product
    ↓
Purchase
    ↓
Receive stock
    ↓
Inventory
    ↓
Sale
    ↓
Inventory update
    ↓
Metrics
    ↓
Deterministic intelligence
    ↓
Insight
    ↓
Action
```

Only what this slice needs (plus identity and organizations as its prerequisite) is built first; complete the slice before widening. See [roadmap.md](roadmap.md).

## Intelligence Boundary

Deterministic intelligence (explicit rules and calculations, no model) is in V1. Statistical and AI-assisted intelligence and automation are not. "Simple, explainable calculations" are in scope; forecasting infrastructure, LLM platforms, agents, vector stores and learned models are not. See [sellable-v1.md](sellable-v1.md#intelligence-layers).

## Explicitly NOT in Sellable V1

- Microservices
- Autonomous AI agents or AI-initiated mutations
- LLM assistant, generated explanations, natural-language questions
- Statistical forecasting and anomaly detection
- Automation of any business action
- Expenses, receipts and file attachments
- Public marketplace listings and public business discovery
- Cross-border marketplace ordering and trade workflows
- A global commercial network as a launch dependency
- International settlement or marketplace payment infrastructure
- Native mobile applications and fully offline operation of the whole application (V1 is connectivity-resilient for its critical workflows; see sellable-v1.md)
- Advanced accounting, payroll, tax compliance and country-specific regulatory integrations
- Persistent wholesale price lists, volume tiers, minimum-order quantities, customer-specific catalogs and self-service B2B ordering
- Multi-location / multi-warehouse transfers (single stock pool per organization in V1; see [data-model.md](../architecture/data-model.md#inventory-integrity-model))
- Merchandise returns/refunds and exchange workflows (void is the V1 correction mechanism)
- Payment processing or automatic financial transactions (payments are recorded, never processed)
- Receipt-printer/payment-terminal integrations
- Third-party integrations and country-specific payment/tax integrations
- Industry-specific modules
- SaaS billing

## Unresolved V1 scope decision: invoice capture

Whether invoice capture, OCR and automatic inventory capture belong in V1 remains undecided under [issue #69](https://github.com/Ulaysu/soforr-business-os/issues/69). It is neither included nor excluded from V1 until the owner decides. Do not implement it or treat it as approved scope before that decision. General file attachments remain PRODUCT-P1 and do not settle the invoice-capture question.

## Scope Rules

- Agents work from [backlog.md](backlog.md); new scope requires human approval.
- Changing this document's V1 boundary is a "major product-scope change" requiring approval (see [AGENTS.md](../../AGENTS.md)).
- Cut scope, not quality: a feature may move out of V1; a quality requirement (tests, security, tenant isolation, integrity) never does.
