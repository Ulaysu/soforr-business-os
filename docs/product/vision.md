# Product Vision

Soforr Business OS is an intelligent operating system for small and medium-sized businesses (SMBs).

It is not another dashboard. It understands what is happening in a business and helps the owner decide what needs attention, reducing the manual work required to operate.

## The Progression

1. **Record** business activity.
2. **Understand** what is happening.
3. **Recommend** useful actions.
4. **Execute** approved actions.
5. **Automate** safe routine operations.

Each stage builds on the previous one. The system must be trustworthy at "record" before it earns the right to "recommend", and at "recommend" before it may "execute". See [ai-architecture.md](../architecture/ai-architecture.md) for how this is enforced.

## Initial Target Customer

An SMB that sells physical products, for example:

- retail businesses
- wholesalers and distributors
- pharmacies
- electronics businesses
- clothing businesses
- hardware businesses
- food businesses

The initial focus is a design choice for the MVP, not a permanent restriction. The core model (products, stock, purchases, sales) must stay industry-neutral. Industry-specific behavior is out of scope until real customers demand it.

## Success Criteria for the First Product

- An owner can record products, purchases and sales with little friction.
- Inventory is always explainable: every quantity traces back to movements.
- The system tells the owner, without being asked, which products will run out and what to reorder.
- The owner approves; the application executes.

## Principles

Engineering rules live in [CLAUDE.md](../../CLAUDE.md). Product-level principles:

- Reduce manual work, do not add software to operate.
- The owner stays in control of high-impact actions.
- Numbers shown to the owner are always deterministic and verified, never model-generated.
