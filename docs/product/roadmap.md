# Roadmap

The roadmap is sequenced by one constraint: **sellability**. The first goal is [Sellable V1](sellable-v1.md): a small, coherent, production-quality Business OS with deterministic operational intelligence that a real customer can use, valuable with no marketplace. Everything after it is chosen from real customer use and evidence.

**Implementation status is not tracked here.** Which items are Done, In Review or in the Backlog is recorded only in [backlog.md](backlog.md). The phases below describe order and outcome, not progress. Long-term positioning is in [vision.md](vision.md).

## Product Strategy: Sold Globally, Configured Regionally

"Global" in this roadmap means the Business OS can be sold to customers in different countries and configured regionally. It does **not** promise a marketplace, a trade network or any cross-border capability at V1 launch (see [what "global" means](vision.md#what-global-means)).

V1 is globally positioned while deliberately narrow in operational scope. The first customer-ready release targets growing retail and light-wholesale businesses anywhere in the world, using a connected web workflow and a single location or stock pool. The domain should not assume Africa, a particular payment rail, a particular tax regime or a particular currency.

Regional capabilities are added through configuration and integrations as customer demand requires them. Global readiness does **not** mean implementing every country, payment provider, tax system, language or enterprise workflow in V1; it means avoiding architecture and product language that make those future markets expensive or unnatural to support.

The product experience is a differentiator: Soforr should combine the simplicity of a focused operations product with deterministic intelligence that tells an owner what needs attention and why.

## Horizon 1: Sellable V1

| Phase | Epics and outcome |
|---|---|
| **0. Foundation** | EPIC 01: solution, persistence, configuration, logging, test infrastructure, CI. |
| **1. Tenancy and access** | EPIC 02: organizations, authentication, authorization, tenant isolation, plus audit events, e-mail verification and password reset, inviting users, rate limiting and API hardening complete the phase. |
| **2. Core operations** | EPIC 03 Catalog, EPIC 04 Inventory, EPIC 05 Purchasing, EPIC 06 Sales. The first vertical slice: Product → Purchase → Receive stock → Inventory → Sale → Inventory update. |
| **3. Deterministic intelligence** | EPIC 09: the intelligence foundation, inventory risk, sales comparisons, then margin, purchasing cost and (with EPIC 07) customer balance and aging, then the actionable insights screen. **Required for V1, not an add-on.** |
| **4. Customers and balances** | EPIC 07: customers, credit sales, recorded payments, balances. Feeds customer intelligence. |
| **5. Screens** | The Vue screens that run the workflow end to end and show the insights. |
| **6. Production readiness** | Deployment, configuration, observability, backup and recovery, security hardening. |
| **7. Commercial readiness** | The first complete journey test, demonstration data, onboarding and pilot readiness. |

Phases 2 to 5 overlap by dependency; the order inside them is in the [backlog sequence](backlog.md#sequence-to-sellable-v1). Complete one strong vertical slice before widening.

**Sellable V1 is complete only when every exit criterion in [sellable-v1.md](sellable-v1.md#v1-exit-criteria) holds.**

## After V1: the Operating Loop

```text
Build → Verify → Deploy → Demonstrate → Sell → Observe → Learn → Improve
```

Do not immediately build dozens of features. Real usage decides what comes next. Then, progressively:

| Stage | Capability | Class |
|---|---|---|
| Product P1 | Expenses, receipts and files, broader reporting, notifications, stored and approvable reorder recommendations, returns/refunds, persistent wholesale pricing, product lifecycle gaps | PRODUCT-P1 |
| Stage 2 | Statistical intelligence: forecasting, demand prediction, anomaly detection beyond simple rules, richer trend analysis | FUTURE |
| Stage 3 | AI-assisted explanations and recommendations; natural-language access through controlled tools (needs approval: paid provider) | FUTURE |
| Stage 4 | Approved automation of routine operations, with human approval for high-impact actions | FUTURE |
| Stage 5 | More autonomous intelligent workflows | FUTURE |
| Commercialization | SaaS billing (needs approval) | FUTURE |

## Growth layers after the Business OS (strategic direction)

These layers are the intended direction from [vision.md](vision.md#direction-from-business-os-to-a-commercial-network). **None is designed, scheduled or implemented, no dates are committed, and none is a V1 dependency.** Each step is gated by evidence (customer validation, business feasibility, market opportunity, operational readiness), not by a calendar.

| Layer | Direction | Status |
|---|---|---|
| 1. Business OS | Sellable V1 and its follow-ups (the phases above) | Current delivery |
| 2. First-country marketplace | Opt-in public representations of products and businesses, business discovery, quotation requests in one validated country. **The country is not selected** and must not be assumed. | Strategic direction; first marketplace milestone is post-V1 |
| 3. Additional country markets | Further logical markets on the same shared platform as evidence and readiness justify | Strategic direction |
| 4. Connected cross-border trade | Validated cross-border offers and trade between markets | Strategic direction |
| 5. Regional and global network | A broader commercial network | Long-term direction |

Before any layer 2 work begins the owner must decide the open questions in [vision.md](vision.md#open-decisions) and approve the architecture for any cross-organisation read path (tenant isolation, [architecture boundaries](../architecture/architecture.md#future-architecture-boundaries-marketplace-and-country-markets)). Invoice capture ([issue #69](https://github.com/Ulaysu/soforr-business-os/issues/69)) is a separate, unresolved owner decision and is not placed in any stage here.

## Cross-cutting (progressive: start early, finish late)

| Epic | Notes |
|---|---|
| EPIC 16: Security and compliance | Baseline in Phase 1; hardening (rate limiting, audit review) before any external user |
| EPIC 17: Observability | Logging in Phase 0; correlation IDs and health checks done; production diagnostics in Phase 6 |
| EPIC 18: Deployment and infrastructure | Docker for local development early; **production deployment requires human approval** |

## Market boundary

V1 is aimed at **growing retail and light-wholesale businesses in any country**, with a deliberately focused first operating model: connectivity-resilient web (critical workflows keep working through outages and synchronize automatically), single location/single stock pool, and no full B2B commerce. Barcode lookup, controlled price overrides and customer credit make the first release useful across both retail checkout and basic wholesale selling. Full B2B pricing catalogs, volume tiers, MOQs, sales orders/quotes, returns/refunds, multi-location inventory and a fully offline application are deliberately later.

## Note on Customers

Sales work without a customer (walk-in sale). Customers attach to sales without changing the slice; their balances and aging feed customer intelligence.
