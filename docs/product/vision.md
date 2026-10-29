# Product Vision

This is the canonical source for Soforr's product identity, positioning, target audience and long-term direction. V1 scope and exclusions are governed by [sellable-v1.md](sellable-v1.md); implementation status lives in [backlog.md](backlog.md). Other documents summarise this one and link here rather than restating it.

> **Direction, not delivery.** This document states where Soforr is heading. It does not say what is built, and it promises no date. Everything beyond [Sellable V1](sellable-v1.md) is intended direction that has not been designed, approved for build or scheduled.

## What Soforr Is

Soforr is a **Business Operating System (Business OS) first**: software that helps product-based businesses run and manage their operations. It records what happens in the business (products, purchasing, stock, sales, customers), keeps that record trustworthy, and tells the owner what needs attention and why.

It is not another dashboard. It understands what is happening in a business and helps the owner decide what needs attention, reducing the manual work required to operate.

> Soforr doesn't just record what happened in your business. It tells you what needs your attention.

## Who It Is For

Soforr serves **product-based businesses**: those that buy, hold, make or sell physical goods. That includes retailers, wholesalers, distributors, importers and manufacturers.

The **initial customer segment is narrower than the long-term audience.** Sellable V1 starts with small and growing retail and light-wholesale businesses ([sellable-v1.md](sellable-v1.md#target-customer-and-market-position)). That is a deliberate first-product choice and not a statement about who Soforr is for.

- The core model (products, stock, purchases, sales) stays industry-neutral. Industry-specific behaviour is out of scope until real customers demand it.
- Listing a business type here does not mean every workflow of that type is supported. In particular, which production workflows manufacturers will eventually receive is **undecided**; V1 has none.

Examples of the product-based businesses V1's core model already fits: retail shops, light wholesalers, pharmacies, electronics, clothing, hardware and food businesses.

## Standalone Value

**The Business OS must be independently valuable, reliable and sellable.** A customer needs a compelling reason to use Soforr even if no marketplace or network exists. Nothing in V1 depends on a marketplace, on other businesses using Soforr, or on network effects. The marketplace is a later growth layer, not a prerequisite for the first sale.

## Direction: From Business OS to a Commercial Network

Soforr's long-term direction is to grow into a **global commercial network** that connects businesses and enables product discovery and trade. The intended progression:

1. **Business OS.** Build and sell a valuable, standalone Business OS (Sellable V1 and its follow-ups).
2. **First-country marketplace.** Validate demand and establish a viable marketplace in an initial country.
3. **Additional country markets.** Expand into further country-specific markets as evidence and operational readiness justify.
4. **Connected cross-border trade.** Connect those markets through validated cross-border trading capabilities.
5. **Regional and global network.** Grow toward a broader regional and global commercial network.

Rules for reading this progression:

- Each step is gated by evidence (customer validation, business feasibility, market opportunity and operational readiness), not by a calendar. No dates are promised.
- **The first marketplace country has not been selected.** It must not be assumed to be The Gambia or any other country. That choice is an open owner decision (see [Open Decisions](#open-decisions)).
- Country-specific markets are intended to be **logical markets on one shared platform**, not separate applications or independently deployed systems, unless an approved architectural decision says otherwise. See [architecture boundaries](../architecture/architecture.md#future-architecture-boundaries-marketplace-and-country-markets).
- Marketplace and network capabilities are not part of Sellable V1 ([sellable-v1.md](sellable-v1.md#explicitly-not-in-v1)).

## What "Global" Means

The word "global" is used in three distinct senses. Documents must say which one they mean.

| Sense | Meaning | Status |
|---|---|---|
| **Sold globally** | The Business OS can be sold to and used by customers in different countries; its core domain does not assume one country, currency, tax regime or payment rail. | V1 intent. |
| **Regionally configurable** | Currency, time zone, locale, tax concepts, addresses and payment providers are configuration or integrations, not assumptions in the core. V1 leaves sound seams; it does not implement every market. | V1 design principle; implemented only as each slice needs it. |
| **A connected network** | Businesses are connected through country-specific marketplaces and, later, cross-border trade. | Long-term direction only. Not designed, not in V1, no date. |

"Global" in V1 documents means the first two senses. It never means a functioning marketplace or trade network at launch.

## Intelligence and the Progression

The product progression is:

1. **Record** business activity.
2. **Understand** what is happening.
3. **Recommend** useful actions.
4. **Execute** approved actions.
5. **Automate** safe routine operations.

Each stage builds on the previous one. The system must be trustworthy at "record" before it earns the right to "recommend", and at "recommend" before it may "execute". See [ai-architecture.md](../architecture/ai-architecture.md) for how this is enforced.

**V1's intelligence is deterministic, explainable and audit-ready**: explicit rules and calculations over the business's own data, each insight showing the facts it was computed from. It is not a chatbot, and V1 does not assume AI is included. We do not postpone intelligence; we postpone unnecessary complexity. Business data is the source of truth, and intelligence is derived from it.

Sellable V1 covers *record* and *understand* (deterministically) and stops at an actionable insight the owner acts on. *Recommend* with approval, *execute* and *automate* come after V1. Statistical, AI-assisted and automated layers are later and are defined in [sellable-v1.md](sellable-v1.md#intelligence-layers).

## Success Criteria for the First Product

- An owner can record products, purchases and sales with little friction.
- Inventory is always explainable: every quantity traces back to movements.
- The system tells the owner, without being asked, what needs attention (for example which products are running low and how many days of stock remain), and shows the facts behind each insight.
- Later, the owner approves a recommendation and the application executes it (after V1).

## Principles

Engineering rules live in [CLAUDE.md](../../CLAUDE.md). Product-level principles:

- Reduce manual work, do not add software to operate.
- The owner stays in control of high-impact actions.
- Numbers shown to the owner are always deterministic and verified, never model-generated.
- The Business OS earns its place on its own merits before any network is built on it.
- A business's operational data is private by default. Anything that becomes visible to other businesses does so by explicit, approved choice.

## Open Decisions

These are unresolved. They stay unresolved until the owner approves an answer; no document should be read as settling them.

| Decision | Where it is tracked |
|---|---|
| Which country hosts the first marketplace | Owner decision, not yet opened as an issue |
| Whether invoice capture, OCR and automatic inventory capture belong in V1, and which extraction technologies (if any) are acceptable | [Issue #69](https://github.com/Ulaysu/soforr-business-os/issues/69) (an invoice-capture decision only; it does not approve any marketplace strategy) |
| If and when an organisation-country attribute is introduced | Owner decision |
| The data model for marketplace participation | Owner decision |
| The approved architecture for cross-organisation reads of published listings | Needs an ADR and owner approval (tenant isolation) |
| Whether users need memberships in several organisations for marketplace workflows | Owner decision (ADR 0002 keeps one organisation per user for the MVP) |
| The currency, exchange-rate and commercial-term model for cross-border trade | Owner decision |
| Whether today's currency invariants (organisation currency fixed at creation, no multi-currency purchasing) are permanent or V1-specific | Owner decision |
| Which production workflows manufacturers will eventually receive | Owner decision |
