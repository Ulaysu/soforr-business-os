# Product Vision

Soforr Business OS is an intelligent operating system for small and medium-sized businesses (SMBs).

It is not another dashboard. It understands what is happening in a business and helps the owner decide what needs attention, reducing the manual work required to operate.

## The Promise

> Soforr doesn't just record what happened in your business. It tells you what needs your attention.

## Two Horizons

- **Sellable V1:** a small, coherent, production-quality Business OS (products, purchasing, inventory, sales, customers) with **deterministic operational intelligence**: explainable insights computed from the business's own data. This is what is built and sold first. See [sellable-v1.md](sellable-v1.md).
- **Intelligent Business OS:** the full vision below: statistical intelligence, AI-assisted explanation, approved automation and more autonomous workflows, grown from V1's trustworthy data and chosen from real customer use.

We do not postpone intelligence; we postpone unnecessary complexity. Deterministic intelligence is part of V1. Business data is the source of truth, and intelligence is derived from it.

## The Progression

1. **Record** business activity.
2. **Understand** what is happening.
3. **Recommend** useful actions.
4. **Execute** approved actions.
5. **Automate** safe routine operations.

Each stage builds on the previous one. The system must be trustworthy at "record" before it earns the right to "recommend", and at "recommend" before it may "execute". See [ai-architecture.md](../architecture/ai-architecture.md) for how this is enforced.

Sellable V1 covers *record* and *understand* (deterministically) and stops at an actionable insight the owner acts on. *Recommend* with approval, *execute* and *automate* come after V1.

## Initial Target Customer

An SMB that sells physical products, for example:

- retail businesses
- wholesalers and distributors
- pharmacies
- electronics businesses
- clothing businesses
- hardware businesses
- food businesses

The initial focus is a design choice for the first product, not a permanent restriction. The core model (products, stock, purchases, sales) must stay industry-neutral. Industry-specific behavior is out of scope until real customers demand it.

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
