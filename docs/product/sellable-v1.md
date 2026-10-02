# Sellable V1

This is the canonical definition of what Soforr must be before it is sold to a first customer, what is deliberately left for later, and how we know V1 is finished. If another document disagrees with this one about V1 scope, this document wins until the owner decides otherwise (see the authority order in [AGENTS.md](../../AGENTS.md)).

> **Terminology.** Older documents and the accepted ADRs say "MVP" for the first product. That is Sellable V1: decisions an ADR makes "for the MVP" apply to V1 and are not reopened by this document.

## The Promise

> Soforr doesn't just record what happened in your business. It tells you what needs your attention.

Sellable V1 is **Business Operations + Deterministic Operational Intelligence**. It is not "CRUD plus reports". The system records the business, keeps that record trustworthy, calculates useful facts from it, and points the owner at the situations that need action, with the facts that justify each one.

## Strategy

- **Sellability is the primary sequencing constraint.** Build the smallest coherent product that solves a real business problem, one vertical slice at a time, and sell it before building the whole vision.
- **Cut scope, not corners.** Features may be deferred; correctness, tenant isolation, authorization, data integrity, transactions, concurrency protection, validation, tests, migrations, error handling and observability are never traded for speed.
- **Do not postpone intelligence. Postpone unnecessary complexity.** Deterministic intelligence is part of V1. Statistical and AI intelligence are not.
- **Business data is the source of truth.** Intelligence is derived from it and never overrides it.
- **After V1, customers decide what is built next** (see [Operating Loop After V1](#operating-loop-after-v1)).

## Two Horizons

| | Horizon 1: Sellable V1 | Horizon 2: Intelligent Business OS |
|---|---|---|
| Purpose | A small, coherent, production-quality Business OS a real business can run on | The full vision: understand, recommend, act, automate |
| Intelligence | Layer 1, deterministic | Layers 2 to 4: statistical, AI-assisted, approved automation |
| Built | Now | After V1 has real customers |

Horizon 2 must grow naturally out of Horizon 1's trustworthy data, so V1 changes nothing about the long-term architecture (see [ai-architecture.md](../architecture/ai-architecture.md)).

## Intelligence Layers

| Layer | What it is | In V1? |
|---|---|---|
| **1. Deterministic** | Explicit business rules, calculations, thresholds, comparisons, derived metrics, rule-based alerts and explainable recommendations over authoritative data. Reproducible. No model. | **Yes, required** |
| 2. Statistical | Forecasting, demand prediction, seasonality, statistical anomaly detection, probabilistic risk | No: after customer validation |
| 3. AI-assisted | LLM explanations, natural-language summaries and questions, contextual recommendations | No |
| 4. Approved automation | Applying approved actions automatically (draft purchase orders, reminders, reports, workflows), under explicit policies and human approval | No |

### What "deterministic intelligence" means

Same data and same clock give the same result, always. Every insight is built from named facts that can be shown to the user, by explicit rules that are documented and tested. There is no learned behavior, no model call, and no hidden state. Each insight has:

```text
Insight:             Rice stock is low.
Facts:               Current stock = 18 units; reorder threshold = 25 units.
Reason:              Current stock is below the reorder threshold.
Recommended action:  Consider replenishing Rice.
```

Vague insights ("your business may be struggling") are not allowed: if the underlying facts cannot be shown, it is not an insight.

### The pipeline in V1

```text
Business transactions
      ↓
Authoritative business data   (products, movements, purchases, sales, customers, payments)
      ↓
Metrics / derived values
      ↓
Deterministic rules
      ↓
Insight (facts + reason)
      ↓
Recommended action           ← V1 stops here; the owner acts
```

The long-term pipeline continues (`Detect → Explain → Recommend → Human approval → Act → new business events`), but V1 deliberately ends at the actionable insight. Intelligence results are computed from authoritative data and are never the record of what happened.

## What Is In Sellable V1

### Core operations (Gate A: customer value)

The workflow that matters more than any single module:

```text
Product → Purchase → Receive stock → Inventory → Sale → Inventory update → Metrics → Insight → Action
```

| Area | In V1 |
|---|---|
| Tenancy and access | Organizations, registration, sign-in/out, roles, per-role permissions, tenant isolation, inviting the organization's users, email verification and password reset |
| Catalog | Products with prices, cost, reorder settings, SKU rule (done) |
| Purchasing | Suppliers, purchase orders, receiving (idempotent, partial), unit cost recorded per purchase line |
| Inventory | Movement-based stock, adjustments with reasons, history, low-stock list |
| Sales | Sales that decrement stock atomically, void, price/cost/name snapshots on every sale line |
| Customers and balances | Customers, optional customer on a sale, credit sales and recorded payments, outstanding balances and aging (recording only; no payment processing). *Proposed; see [Decisions needing approval](#decisions-needing-the-owners-approval).* |
| Audit | Append-only audit events for business-sensitive actions |
| Basic metrics | Sales totals and period comparisons, stock list, as the data behind the intelligence |
| User interface | The screens needed to run the workflow end to end (Vue) |

### Deterministic intelligence (V1 requirements)

All computed from authoritative data with documented formulas; the exact formula of each is fixed in its backlog item and covered by tests with a fixed clock.

| Capability | Examples of what the owner sees |
|---|---|
| **Inventory risk** | Product below its reorder threshold; estimated stock coverage in days from recent sales velocity; products at risk of running out before a reorder could arrive; suggested reorder quantity with its basis |
| **Sales** | Revenue for the current period against the previous period of equal length, with the percentage change; quantity sold; best-selling and poorly-performing products; products with declining sales |
| **Product margin** | Selling price, cost, gross margin and margin percentage from the sale-time snapshots; margin change between periods and what changed (for example purchase cost) |
| **Purchasing cost** | Change in a product's purchase cost against the previous purchase; purchase volume and recent activity; supplier cost change where several suppliers exist |
| **Customer balances** | Outstanding balance per customer; overdue balance; aging buckets; customers over a configured threshold |
| **Actionable insights** | One place ("Things that need your attention") that lists the current insights with their facts and a recommended action, most important first |

Expense intelligence (totals, period comparisons, threshold alerts) is **not** a V1 requirement because expense recording is not (see below).

### Production readiness (Gate C)

Credible deployment, configuration, migrations, health checks, logging and diagnostics, monitoring, backup and recovery approach, security hardening (rate limiting, headers, CORS, email verification), and written deployment documentation. Not enterprise perfection: a real customer must be able to use the product without it being obviously experimental.

### Commercial readiness (Gate D)

A customer can understand what Soforr does, who it is for, what they get and how they start. The product can be demonstrated end to end on realistic data, there is an onboarding path, and the known limitations are written down.

## Explicitly Not In V1

| Deferred | Why | Where it goes |
|---|---|---|
| Expense recording, receipts and file attachments | Not needed for the core workflow or the first sale | Product P1 |
| Broader reporting beyond the V1 metrics | Metrics behind the insights cover the first customer | Product P1 |
| Stored, approvable reorder recommendations and one-click purchase order creation from a recommendation | V1 shows the recommendation and its facts; the owner raises the purchase order | Product P1 |
| In-app notifications | The insight list is the V1 surface | Product P1 |
| Product reactivation, concurrent-edit protection on products, richer audit viewing | Open lifecycle decisions, no first-customer need | Product P1 |
| Statistical forecasting, anomaly detection, demand prediction | Layer 2 | Future |
| LLM assistant, natural-language questions, generated explanations | Layer 3; needs approval (paid service) | Future |
| Automation and autonomous workflows | Layer 4 | Future |
| SaaS billing, advanced accounting, payroll, multi-warehouse, industry-specific modules, mobile apps, third-party integrations | Out of scope until real customers demand them | Future |

Nothing in this list may be built to claim that Soforr is "AI-powered". There is no LLM platform, agent, vector store, embedding pipeline or forecasting infrastructure in V1.

## V1 Exit Criteria

Soforr is not Sellable V1 until **all** of these hold, each verified rather than assumed.

**Core operations**
- Organization/tenant model, authentication and authorization work
- Products, purchasing and inventory receiving work; inventory movements are reliable
- Sales work and inventory updates correctly after a sale
- Customers and balances work (as scoped in V1)

**Deterministic intelligence**
- Inventory risk, stock coverage and reorder calculations work
- Sales metrics and period comparisons work
- Margin calculations work where cost and sales data exist
- Purchasing cost intelligence works where purchase data exists
- Customer aging and balance intelligence works
- Every insight is traceable to the underlying data and shows its facts
- Intelligence is covered by tests
- The actionable insight experience exists in the UI

**Engineering quality**
- Critical workflows have integration tests, including the first complete journey
- Domain invariants, tenant isolation and authorization are tested
- Inventory concurrency and integrity are protected and tested
- Error handling is consistent; logging and observability exist

**Production readiness**
- Production configuration, database deployment and migrations, the deployment process and the backup and recovery approach are documented
- Health checks exist; security hardening for V1 is complete
- A production environment can be deployed reproducibly

**Commercial readiness**
- The product can be demonstrated end to end; a real business can follow the workflow
- An onboarding path exists; scope and known limitations are documented
- The product is suitable for a first pilot customer

## Sequencing Rule

For every proposed task ask: **does this materially move Soforr closer to a sellable product?** Prioritize work that enables the core workflow, provides required deterministic intelligence, fixes correctness, improves security, protects tenant isolation or data integrity, improves required testing, enables production deployment, or removes a critical dependency. Deprioritize work that is interesting but unnecessary, expands scope without customer value, adds premature abstraction, builds advanced AI before the data exists, builds speculative infrastructure, or optimizes prematurely.

The backlog classes ([backlog.md](backlog.md)):

| Class | Meaning |
|---|---|
| **SELLABLE-P0** | Required to make the first customer-ready product |
| **PRODUCT-P1** | Valuable product capability, not required for the first sale |
| **FUTURE** | Long-term capability (Horizon 2 and beyond) |

Within SELLABLE-P0, the order is by dependency along the first vertical slice; complete the slice before widening.

## Operating Loop After V1

```text
Build → Verify → Deploy → Demonstrate → Sell → Observe → Learn → Improve
```

After V1, do not start dozens of features. Use real customer usage to choose what comes next, and then, progressively: Stage 2 statistical intelligence, Stage 3 AI-assisted explanations and recommendations, Stage 4 approved automation, Stage 5 more autonomous intelligent workflows.

## Decisions Needing the Owner's Approval

This reorientation proposes these product-scope choices. They are written into the backlog as proposed and are easy to change; none needs code to be reverted.

1. **Customers with credit sales and recorded payments are in V1** (needed for customer balance and aging intelligence, which the V1 intelligence list names). If the first customers sell only for cash, this can move to Product P1 and customer intelligence with it.
2. **Expenses are Product P1**, not V1, so expense intelligence is P1 as well.
3. **Reorder recommendations are shown as insights in V1; stored/approvable recommendations and one-click purchase order creation are Product P1.** This moves the old "owner approval" step out of the first slice.
4. **Inviting an organization's users (IDENTITY-005) is in V1.** Roles and permissions are built but there is currently no way to give a second person access; a business with staff cannot use the product without it.
