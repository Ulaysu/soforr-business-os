# Product Scope

## Initial Modules (MVP candidates)

| Module | Purpose |
|---|---|
| Identity & Access | Users, authentication, roles, permissions |
| Organizations | Tenants; membership; organization settings |
| Products / Catalog | What the business sells and buys |
| Inventory | Stock levels derived from recorded movements |
| Sales | Recording sales and their effect on stock |
| Customers | Who the business sells to |
| Purchasing | Buying stock from suppliers; receiving goods; reorder approval |
| Expenses | Recording non-inventory business costs |
| Reporting | Verified summaries of sales, stock, expenses |
| Automation | Safe, rule-based routine operations |
| Intelligence | Stock forecasting and reorder recommendations; later the AI assistant |
| Notifications | Telling the owner what needs attention |
| Files / Receipts | Attachments such as receipts and invoices |

Module boundaries are defined in [modules.md](../architecture/modules.md).

## First Vertical Slice

The first end-to-end flow. Its purpose is to prove the system can move from raw business activity to useful operational intelligence.

```text
Product
    ↓
Purchase
    ↓
Inventory
    ↓
Sale
    ↓
Inventory Update
    ↓
Stock Forecast
    ↓
Reorder Recommendation
    ↓
Owner Approval
```

Only modules required by this slice (plus Identity/Organizations as its prerequisite) are built first. See [roadmap.md](roadmap.md).

## Future Areas (not MVP)

- SaaS Billing (subscriptions, plans, invoicing of tenants)
- Advanced accounting (ledgers, double-entry, tax filing)
- Payroll
- Additional integrations (payment providers, e-commerce, accounting software)
- Advanced industry-specific functionality (e.g., pharmacy batch/expiry regulation)

## Explicitly NOT in the MVP

- Microservices
- Autonomous AI agents or AI-initiated mutations
- Mobile applications
- Advanced accounting, payroll, tax compliance
- Multi-warehouse transfers (single stock location per organization initially; see [data-model.md](../architecture/data-model.md#inventory-integrity-model))
- Payment processing or automatic financial transactions
- Third-party integrations
- Sophisticated forecasting (simple, explainable velocity-based forecasting only)
- Industry-specific modules
- SaaS billing

## Scope Rules

- Agents work from [backlog.md](backlog.md); new scope requires human approval.
- Changing this document's MVP boundary is a "major product-scope change" requiring approval (see [AGENTS.md](../../AGENTS.md)).
