# Soforr Business OS

Soforr Business OS is an intelligent business operations platform designed to reduce the amount of manual work required to run a small or medium-sized business.

The system combines:

- Sales
- Inventory
- Purchasing
- Customers
- Expenses
- Business intelligence
- Automation
- Notifications
- Operational recommendations

The goal is not to build another dashboard.

The goal is to build a system that understands what is happening in a business and helps the owner decide what needs attention.

## Product Promise

Soforr doesn't just record what happened in your business. It tells you what needs your attention.

The first release (**Sellable V1**) is a focused Business OS with deterministic, explainable operational intelligence: see [docs/product/sellable-v1.md](docs/product/sellable-v1.md). The long-term vision (statistical and AI-assisted intelligence, approved automation) grows from it after real customers use it.

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