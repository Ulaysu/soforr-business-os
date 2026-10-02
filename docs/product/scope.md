# Product Scope

The product is sequenced toward a **Sellable V1** ([sellable-v1.md](sellable-v1.md) is the canonical definition: what is in, what is deferred and the exit criteria). This document lists the modules and where each stands. Module boundaries are in [modules.md](../architecture/modules.md); work items and their priority classes are in [backlog.md](backlog.md).

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
| Products / Catalog | What the business sells and buys | SELLABLE-P0 |
| Inventory | Stock levels derived from recorded movements | SELLABLE-P0 |
| Purchasing | Buying stock from suppliers; receiving goods | SELLABLE-P0 |
| Sales | Recording sales and their effect on stock | SELLABLE-P0 |
| Customers | Who the business sells to; credit sales, recorded payments, balances (proposed for V1, see [sellable-v1.md](sellable-v1.md#decisions-needing-the-owners-approval)) | SELLABLE-P0 |
| Intelligence (deterministic) | Rule-based, explainable insights from business data: inventory risk, sales comparisons, margins, purchasing cost, customer aging, and the "things that need your attention" view | SELLABLE-P0 |
| Production readiness | Deployment, configuration, observability, backup and recovery | SELLABLE-P0 |
| Expenses | Recording non-inventory business costs (and expense insights) | PRODUCT-P1 |
| Reporting beyond the V1 metrics | Further verified summaries | PRODUCT-P1 |
| Notifications | Telling the owner what needs attention outside the app | PRODUCT-P1 |
| Files / Receipts | Attachments such as receipts and invoices | PRODUCT-P1 |
| Stored, approvable reorder recommendations | Recommendations as records the owner approves, creating a purchase order | PRODUCT-P1 |
| Statistical intelligence | Forecasting, demand prediction, statistical anomaly detection | FUTURE |
| AI assistant | Natural-language access, generated explanations (over the deterministic services) | FUTURE |
| Automation | Safe, approved routine operations | FUTURE |

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
- Mobile applications
- Advanced accounting, payroll, tax compliance
- Multi-warehouse transfers (single stock location per organization; see [data-model.md](../architecture/data-model.md#inventory-integrity-model))
- Payment processing or automatic financial transactions (payments are recorded, never processed)
- Third-party integrations
- Industry-specific modules
- SaaS billing

## Scope Rules

- Agents work from [backlog.md](backlog.md); new scope requires human approval.
- Changing this document's V1 boundary is a "major product-scope change" requiring approval (see [AGENTS.md](../../AGENTS.md)).
- Cut scope, not quality: a feature may move out of V1; a quality requirement (tests, security, tenant isolation, integrity) never does.
