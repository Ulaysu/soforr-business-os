# Soforr Business OS

Soforr is a Business Operating System (Business OS) first, for product-based businesses: retailers, wholesalers, distributors, importers and manufacturers. It is designed to reduce the manual work required to run the business. Sellable V1 prioritizes small and medium-sized wholesalers and distributors, while retaining basic retail and light-wholesale sales support. It must be valuable and sellable on its own, with no marketplace. Its long-term direction is to grow into a global commercial network; that direction is described, with its limits, in [docs/product/vision.md](docs/product/vision.md), the canonical source for positioning.

Over time the system combines the areas below. **Sellable V1** includes sales, inventory, purchasing, customers and deterministic business intelligence; expenses, notifications, stored recommendations and automation are later ([sellable-v1.md](docs/product/sellable-v1.md)).

- Sales
- Inventory
- Purchasing
- Customers
- Expenses (later)
- Business intelligence (deterministic in V1)
- Automation (later)
- Notifications (later)
- Operational recommendations (insights in V1; stored and approvable recommendations later)

The goal is not to build another dashboard.

The goal is to build a system that understands what is happening in a business and helps the owner decide what needs attention.

## Product Promise

Soforr doesn't just record what happened in your business. It tells you what needs your attention.

The first release (**Sellable V1**) is a focused Business OS with deterministic, explainable operational intelligence: see [docs/product/sellable-v1.md](docs/product/sellable-v1.md), which governs V1 scope and exclusions (no marketplace). Later layers (statistical and AI-assisted intelligence, approved automation, and a marketplace and commercial network) grow from it after real customers use it; they are direction, not current features. Implementation status is in [the backlog](docs/product/backlog.md).

## Product Principle

Record activity → Understand activity → Recommend actions → Execute approved actions → Automate safe routine operations.

## Initial Technology

- C#
- ASP.NET Core
- Entity Framework Core
- PostgreSQL
- Vue
- TypeScript
- Docker
- REST API

## Architecture

Soforr Business OS starts as a modular monolith.

The system will have clear internal module boundaries without prematurely splitting the application into microservices.

## Initial Vertical Slice

The first complete business workflow is:

Product
→ Purchase
→ Receive Stock
→ Inventory
→ Sale
→ Inventory Update
→ Metrics
→ Deterministic Insight
→ Action

## Documentation

- Product: [vision](docs/product/vision.md), [Sellable V1](docs/product/sellable-v1.md), [scope](docs/product/scope.md), [roadmap](docs/product/roadmap.md), [backlog](docs/product/backlog.md)
- Architecture: [architecture](docs/architecture/architecture.md), [modules](docs/architecture/modules.md), [data model](docs/architecture/data-model.md), [AI architecture](docs/architecture/ai-architecture.md)
- Engineering: [development](docs/engineering/development.md), [testing](docs/engineering/testing.md), [definition of done](docs/engineering/definition-of-done.md), [intelligence conventions](docs/engineering/intelligence.md)
- Security: [security](docs/security/security.md), [threat model](docs/security/threat-model.md)
- Decisions: [ADRs](docs/decisions/README.md)
- Agent rules: [CLAUDE.md](CLAUDE.md), [AGENTS.md](AGENTS.md)

## Development Philosophy

The system should prioritize:

- Simplicity
- Strong domain boundaries
- Data integrity
- Security
- Testability
- Observability
- Automation
- Maintainability
- Human approval for high-impact actions