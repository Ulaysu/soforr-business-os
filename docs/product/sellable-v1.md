# Sellable V1

This is the canonical definition of what Soforr must be before it is sold to a first customer, what is deliberately left for later, and how we know V1 is finished. This document governs V1 product scope and exclusions. Product identity, positioning and long-term direction are governed by [vision.md](vision.md); implementation status is tracked in [backlog.md](backlog.md); Accepted ADRs govern the architecture decisions they record (authority order: [AGENTS.md](../../AGENTS.md)). Where another document disagrees with this one about V1 scope, this document wins until the owner decides otherwise.

**Sellable V1 is independently valuable.** It must give a customer a compelling reason to buy and use Soforr with no marketplace, no network and no other Soforr customers. Marketplace and network capabilities are a later growth layer ([vision.md](vision.md#direction-from-business-os-to-a-commercial-network)) and are never a V1 dependency.

> **Terminology.** Older documents and the accepted ADRs say "MVP" for the first product. That is Sellable V1: decisions an ADR makes "for the MVP" apply to V1 and are not reopened by this document.

## The Promise

> Soforr doesn't just record what happened in your business. It tells you what needs your attention.

Sellable V1 is **Business Operations + Deterministic Operational Intelligence**. It is not "CRUD plus reports". The system records the business, keeps that record trustworthy, calculates useful facts from it, and points the owner at the situations that need action, with the facts that justify each one.

## Target customer and market position

V1 is designed for **small and growing retail and light-wholesale businesses in any country** (the initial customer segment of a product-based Business OS; see [who Soforr is for](vision.md#who-it-is-for)) that need one operational source of truth for products, purchasing, stock, sales and customer balances. The first implementation intentionally starts with a **single location or single stock pool**, but that is a scope boundary for V1—not a geographic or long-term product limitation.

Soforr is sold globally and regionally configurable (see [what "global" means](vision.md#what-global-means)); it is not an Africa-specific POS. A business in Banjul, London, New York, Toronto, Lagos, Dubai or elsewhere should recognize the same core product: a simple operating system for running a growing product-based business. Regional currencies, payment methods, tax rules, address conventions and other local requirements belong in configuration or integrations rather than assumptions embedded in the core domain.

The product is intentionally **retail-and-light-wholesale**, not a full enterprise POS/ERP or B2B commerce portal. Retail software commonly combines inventory, sales, customer data and analytics; wholesale systems add account-specific pricing, quantity rules and payment terms. Soforr competes through a simpler experience and a stronger operational-intelligence layer rather than by copying every feature of mature enterprise suites.

For the first release, Soforr is **connectivity-resilient** and web-based: critical operational workflows (sales, credit sales and recorded payments) continue during temporary network outages, with durable local persistence and automatic synchronization when connectivity returns. The server stays the source of truth; the browser keeps an operational copy and a queue of pending operations ([ADR 0007](../decisions/0007-offline-resilience.md)). Multi-location inventory, native mobile POS, payment-terminal integrations and advanced wholesale pricing are deliberately deferred because they materially increase complexity. They are product boundaries for V1, not permanent limitations or regional omissions.

## Global product principles

- **Sold globally, configured regionally.** "Global" in V1 means the product can be sold in different countries and configured for them; it does not mean a marketplace or trade network at launch. Product language, core workflows and domain concepts must make sense across markets; regional behavior is configuration or integration unless it is truly universal.
- **Global-ready, not globally bloated.** V1 should leave sound architectural seams for currencies, locales, time zones, taxes, payment providers, locations and integrations without implementing every market before customer validation. Country, currency, time zone and locale are distinct concepts ([architecture boundaries](../architecture/architecture.md#future-architecture-boundaries-marketplace-and-country-markets)).
- **Simple enough to love.** Soforr should feel substantially easier to understand and operate than a traditional ERP while still being trustworthy enough to run real business operations.
- **Intelligence is part of the product experience.** The owner should not have to interpret a wall of reports to discover important operational problems; deterministic insights should surface what deserves attention and show why.

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
| Catalog | Products with prices, cost, reorder settings, SKU rule (done), optional barcode/GTIN for fast lookup |
| Purchasing | Suppliers, purchase orders, receiving (idempotent, partial), unit cost recorded per purchase line |
| Inventory | Movement-based stock, adjustments with reasons, history, low-stock list |
| Sales | Fast sales that decrement stock atomically, barcode/SKU lookup, controlled line-price overrides/discounts, void, price/cost/name snapshots on every sale line |
| Customers and balances | Customers, optional customer on a sale, credit sales and recorded payments, outstanding balances and aging (recording only; no payment processing). Accepted: see [Accepted V1 product decisions](#accepted-v1-product-decisions), decision 1. |
| Audit | Append-only audit events for business-sensitive actions |
| Basic metrics | Sales totals and period comparisons, stock list, as the data behind the intelligence |
| User interface | The screens needed to run the workflow end to end (Vue) |

### Deterministic intelligence (V1 requirements)

All computed from authoritative data with documented formulas; the exact formula of each is fixed in its backlog item and covered by tests with a fixed clock.

| Capability | Examples of what the owner sees |
|---|---|
| **Inventory risk** | Product below its reorder threshold; estimated stock coverage in days from recent sales velocity; products at risk of running out before a reorder could arrive; suggested reorder quantity with its basis |
| **Sales** | Revenue and quantity for the current period against the previous period of equal length; top sellers; slow movers; products with meaningful sales decline; discount/price-override impact where sale-line data exists |
| **Product margin** | Selling price, cost, gross margin and margin percentage from the sale-time snapshots; margin change between periods and what changed (for example purchase cost) |
| **Purchasing cost** | Change in a product's purchase cost against the previous purchase; purchase volume and recent activity; supplier cost change where several suppliers exist |
| **Customer balances** | Outstanding balance per customer; overdue balance; aging buckets; customers above an explicit organization-configured materiality threshold |
| **Actionable insights** | One place ("Things that need your attention") that lists current insights with facts, reason and a suggested next action, ordered by deterministic severity |

Expense intelligence (totals, period comparisons, threshold alerts) is **not** a V1 requirement because expense recording is not (see below).

### Retail and light-wholesale controls

V1 includes the minimum controls needed to sell credibly to both sides of the market without turning Soforr into a full commerce platform:

- **Product identification:** SKU remains the business identifier; an optional barcode/GTIN may be attached to a product and used for search and fast checkout. A product has one primary barcode in V1; multiple barcodes, case/unit barcodes and advanced 2D/GS1 data are later.
- **Price integrity:** the product selling price is the default. A sale may use an explicit line-price override or discount when permitted by the user's permission. The sale line stores the final applied price and, where applicable, the original price and reason/actor so margin and audit data remain explainable.
- **Wholesale reality without wholesale-platform complexity:** customers can buy on credit and sales can use negotiated line prices. Persistent customer price lists, quantity breaks, minimum-order quantities, customer-specific catalogs and self-service B2B ordering are Product P1/Future until real customers justify them. These are established wholesale requirements, so they are deliberately named rather than accidentally omitted.
- **Retail reality without a full POS stack:** the web sale screen is designed for rapid product lookup and barcode-scanner input, and it keeps working through a connectivity outage (see Connectivity resilience below). V1 does not promise payment-terminal integration, receipt-printer integration or a native mobile app.
- **Returns:** ordinary corrections use void. A proper merchandise-return/refund workflow is Product P1 because it requires explicit refund, stock disposition and fraud-control rules rather than pretending a void is a return.

This makes the commercial claim honest: **Soforr V1 is a global, connectivity-resilient operations system for growing retail and light-wholesale businesses: critical operations continue offline and synchronize automatically when connectivity returns. It is not a fully offline application, a multi-location platform or a full B2B commerce suite.**

## Production readiness (Gate C)

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
| Invoice capture, OCR and automatic inventory capture from supplier invoices | **Unresolved owner decision**, not classified as in or out of V1. See [Open decisions affecting scope](#open-decisions-affecting-scope) | Undecided ([issue #69](https://github.com/Ulaysu/soforr-business-os/issues/69)) |
| Public marketplace listings and public business discovery | Marketplace is a later growth layer, not a V1 dependency | Future |
| Cross-border marketplace ordering and trade workflows | Requires validated country markets first | Future |
| A global commercial network as a launch dependency | V1 must be sellable without it | Future |
| International settlement and marketplace payment infrastructure | V1 records payments and never processes them | Future |
| Statistical forecasting, anomaly detection, demand prediction | Layer 2 | Future |
| LLM assistant, natural-language questions, generated explanations | Layer 3; needs approval (paid service) | Future |
| Automation and autonomous workflows | Layer 4 | Future |
| Persistent wholesale price lists, volume pricing, minimum-order quantities, customer catalogs, sales orders/quotes, returns/refunds, multi-location inventory, native mobile apps, payment-terminal integrations and third-party integrations | Valuable market capabilities, but each adds meaningful workflow/state complexity; build from observed customer demand | Product P1 / Future |

No marketplace entity, listing, country-market or cross-border model is built for V1; [architecture boundaries](../architecture/architecture.md#future-architecture-boundaries-marketplace-and-country-markets) are principles, not permission to implement.

Nothing in this list may be built to claim that Soforr is "AI-powered". There is no LLM platform, agent, vector store, embedding pipeline or forecasting infrastructure in V1.

## Connectivity resilience (offline-capable critical workflows)

**The promise:** critical business operations continue offline; cloud synchronization resumes automatically when connectivity returns. Not the whole Business OS is offline.

| Offline-capable in V1 | Online-required in V1 |
|---|---|
| Product lookup and search (SKU, barcode) from the cached catalog | Organization settings and administration |
| Customer lookup from the cached customers | User invitations, role and permission changes |
| Sales, including credit sales to cached customers | Sign-in, e-mail verification, password reset |
| Recording payments against synced sales | Creating products, customers and suppliers |
| Local history of recent operations, the sync queue and its status | Purchasing, receiving, stock adjustments, voids and payment reversals |
| Cached insights shown with their freshness time | Insight recalculation, reporting, anything needing fresh server-wide data |

The rules that make this safe (decided with the owner on 2026-10-08, detailed in ADR 0007): the server stays the source of truth and the browser holds a bounded operational copy plus a durable outbox; a sale keeps the time it happened on the device, accepted only inside an offline authorization the server established at an online validation (a timestamp inside 72 hours is not enough on its own; an invalid one is refused, never silently re-dated), and the server records when it received it; a normal catalog price that changed while offline is a conflict that needs attention, never a silent re-price, and a legitimate SALES-004 override is not mistaken for that; an offline session lasts at most 72 hours after the last online validation and never stores a credential; unsynchronized operations are bound to their user and organization, and sign-out is blocked while any are pending; replays reuse the existing idempotent commands (sale, payment), so a retry never duplicates a sale or a payment; conflicts (such as insufficient authoritative stock) are surfaced, never resolved silently.

## V1 Exit Criteria

Soforr is not Sellable V1 until **all** of these hold, each verified rather than assumed.

**Core operations**
- Organization/tenant model, authentication and authorization work
- Products, purchasing and inventory receiving work; inventory movements are reliable
- Products can be found by SKU and, when configured, by barcode/GTIN
- Sales work and inventory updates correctly after a sale
- Price overrides/discounts are permission-controlled and auditable
- Customers and balances work
- An operator can complete the core workflow without relying on spreadsheets, including during a temporary outage

**Offline reliability**
- A previously authenticated user can operate the critical workflows (sales, credit sales, payments) during temporary connectivity loss
- Offline sales survive a browser refresh and restart, and are durably queued before the UI says they are saved
- Restoring connectivity triggers synchronization automatically; the user can also sync on demand
- Synchronization is idempotent: duplicate network retries, including a lost response after the server committed, cannot duplicate sales or payments
- Inventory integrity is preserved and the server remains authoritative; offline stock is only an estimate and is labelled as one
- Conflicts (insufficient stock, changed price, inactive product or customer, changed permission) are surfaced to the user rather than resolved silently
- Tenant isolation is preserved in the local store and during synchronization; a queued operation can never reach another organization or be sent by another user
- Offline authentication does not create an indefinite bypass: the offline window is bounded, backdating a transaction is refused by the server, and no credential is stored
- The UI always shows whether the device is online, whether a change is saved, whether it is synchronized, what is waiting and what needs attention
- The critical offline scenarios are covered by automated tests, and a real-browser offline journey has been verified (refresh, restart, lost response, reconnect)

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

## V1 deterministic formula contract

The following defaults are part of V1 unless a backlog item explicitly supersedes them:

- **Sales periods:** organization-local time zone; current period is compared with the immediately preceding period of equal duration.
- **Sales growth:** `(current - previous) / previous × 100` when the previous value is non-zero. When the previous value is zero, report "no comparable previous sales" rather than infinity.
- **Slow mover:** among active products with sufficient data, the bottom 20% by quantity sold for the selected period. If fewer than 5 products qualify, show the ordered list rather than inventing a percentile.
- **Declining sales:** sales exist in both comparison periods and quantity sold decreased by at least 20%.
- **Stock coverage:** `current stock / average daily quantity sold` over the trailing 30 calendar days. No sales means "no recent sales", not infinite coverage.
- **Reorder target:** when both reorder point and lead time exist, `target stock = reorder point + (average daily sales × lead-time days)`. Suggested reorder quantity is `max(0, target stock - current stock)`. If required inputs are missing, no quantity is suggested.
- **Margin:** V1 uses the sale-line cost snapshot as the cost basis. It is a management margin measure, not accounting-grade COGS and does not implement FIFO, weighted-average costing or inventory valuation.
- **Customer aging:** age from each sale's explicit due date. Default credit terms may exist at organization level; customer-specific terms override them. Buckets are 0–30, 31–60, 61–90 and 91+ days past due.
- **Materiality threshold:** customer balance insights use an explicit organization setting; no hidden hard-coded monetary threshold.
- **Insight severity:** severity is rule-derived, not an LLM judgment. Stockout risk outranks low-stock, overdue balances outrank ordinary balances, and material margin/cost changes outrank informational changes. Exact precedence belongs to INTEL-007 and is tested.

These rules are intentionally simple. They make V1 reproducible and explainable; later statistical intelligence can replace or augment them only after customer evidence justifies it.

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

## Accepted V1 product decisions

These are canonical product-scope decisions for the first sellable release:

1. **Customers, credit sales and recorded payments are in V1.** Wholesale and many retail businesses need customer balances and aging; payments are recorded only and are not processed by Soforr.
2. **Expenses are Product P1.** They are useful but are not required to prove the core inventory/purchasing/sales value proposition.
3. **Reorder recommendations are insights in V1.** Stored/approvable recommendations and one-click purchase-order creation are Product P1.
4. **Inviting organization users is in V1.** Roles without an invitation workflow are not practically usable for a business with staff.
5. **Barcode/GTIN lookup is in V1.** One primary barcode per product is the deliberately narrow first implementation; multiple identifiers and advanced barcode workflows are later.
6. **V1 supports light wholesale, not full B2B commerce.** Negotiated sale-line pricing and customer credit are supported. Persistent price lists, volume tiers, MOQs, customer-specific catalogs, quotes and self-service ordering remain outside V1.
7. **V1 is connectivity-resilient (revised 2026-10-08, owner decision).** Critical workflows continue offline and synchronize automatically; the server stays authoritative ([ADR 0007](../decisions/0007-offline-resilience.md)). Native mobile POS, multi-location stock and payment-terminal integrations are not V1 requirements. This is an explicit product boundary, not an accidental omission. This replaces the earlier decision that V1 is connected-first with offline synchronization deferred.

Any later change goes through the major product-scope approval rule in `AGENTS.md`.

## Open decisions affecting scope

These are unresolved. They remain unresolved until the owner explicitly approves an answer; nothing here is a decision.

### Invoice capture, OCR and automatic inventory capture ([issue #69](https://github.com/Ulaysu/soforr-business-os/issues/69))

Whether capturing purchase information from supplier invoices is a V1 sellability requirement or a post-V1 differentiator is **undecided**. Issue #69 is the decision thread for this question only; it does not approve or record any marketplace strategy. Until the owner decides:

- Invoice capture is neither in V1 nor excluded from it. The generic Files / Receipts capability stays PRODUCT-P1 (see the table above), and invoice capture is not a reason to pull general attachments into V1.
- Whether OCR or other extraction counts as the "AI" that V1 excludes, and which extraction technologies (if any) are acceptable, is undecided. It is not classified as permitted or forbidden.
- Constraints already stated in #69 apply to any future design: extraction must never change authoritative inventory or financial records without user confirmation and the existing server-side validation; a workflow definition must cover supported file types, extracted fields, uncertainty handling, user review, duplicate detection and the exact moment a confirmed invoice creates a purchase, receipt or stock change; and no paid service or credential is added without owner approval (`CLAUDE.md` §21).
- The decision is to be informed by a test with real supplier invoices and target customers.

### Other open decisions

Decisions about the first marketplace country, organisation country, marketplace participation, cross-organisation reads of published listings, multi-organisation memberships, cross-border currency and terms, whether today's currency invariants are permanent, and manufacturer production workflows are listed in [vision.md](vision.md#open-decisions). None is needed for V1.
