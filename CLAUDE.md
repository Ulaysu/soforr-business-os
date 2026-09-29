# Soforr Business OS — Claude Code Instructions

## 1. Role

You are the primary implementation agent for Soforr Business OS.

You are responsible for implementing, testing, refactoring, and documenting the software according to the product and engineering rules defined in this document.

You are NOT the product owner.

Do not independently change the product direction, architecture, business rules, or scope without explicit approval.

When requirements are unclear, stop and ask for clarification rather than inventing important business behavior.

---

# 2. Product Vision

Soforr Business OS is an intelligent business operating system for small and medium-sized businesses.

The goal is not to build another dashboard.

The goal is to build a system that understands what is happening in a business and helps the owner determine what needs attention.

The system should progressively move through:

Record activity
→ Understand activity
→ Recommend actions
→ Execute approved actions
→ Automate safe routine operations

The product should reduce the amount of manual operational work required from business owners.

---

# 3. Initial Product Scope

The initial system will support:

- Organizations
- Users and access control
- Products
- Inventory
- Sales
- Customers
- Purchasing
- Expenses
- Reporting
- Notifications
- Automation
- Business intelligence
- AI-assisted recommendations
- File and receipt management

The first major workflow is:

Product
→ Purchase
→ Inventory
→ Sale
→ Inventory Update
→ Stock Analysis
→ Reorder Recommendation
→ Owner Approval

Do not build every module at once.

Build complete vertical slices and keep the system usable at every meaningful stage.

---

# 4. Architecture

Soforr Business OS will initially be a modular monolith.

Do NOT introduce microservices unless explicitly approved.

The system should have strong internal module boundaries while remaining a single deployable application initially.

Expected backend structure:

src/
├── Soforr.Api/
├── Soforr.Application/
├── Soforr.Domain/
├── Soforr.Infrastructure/
└── Soforr.Web/

Expected responsibilities:

### Soforr.Api

Responsible for:

- HTTP endpoints
- Authentication configuration
- Authorization configuration
- Request/response handling
- API middleware
- API-level validation
- Dependency injection composition

Controllers/endpoints should remain thin.

Business rules do not belong here.

### Soforr.Application

Responsible for:

- Use cases
- Commands
- Queries
- Application services
- DTOs
- Validation
- Application-level orchestration

Examples:

- CreateProduct
- CreateSale
- ReceivePurchase
- AdjustInventory
- CreateExpense
- GenerateReorderRecommendation
- ApproveReorder

### Soforr.Domain

Responsible for:

- Domain entities
- Value objects
- Domain rules
- Domain events where appropriate
- Business invariants

The Domain layer must not depend on:

- ASP.NET Core
- Entity Framework Core
- PostgreSQL
- Vue
- External AI providers
- Infrastructure implementations

The domain should remain as independent as reasonably possible.

### Soforr.Infrastructure

Responsible for:

- Entity Framework Core
- PostgreSQL
- Database configuration
- External services
- File storage
- Email
- AI provider implementations
- Other infrastructure concerns

Infrastructure implements interfaces required by the Application or Domain layers.

### Soforr.Web

Responsible for:

- Vue application
- User interface
- Client-side state
- API communication
- User interaction
- Presentation

Business-critical rules must not exist only in the frontend.

---

# 5. Dependency Direction

Maintain the following dependency direction:

Api
→ Application
→ Domain

Infrastructure
→ Application
→ Domain

Domain
→ nothing outside the Domain layer

Application
→ Domain

The Domain must never depend on Infrastructure or API.

Avoid circular dependencies.

---

# 6. Technology

Use the following initial technology stack unless explicitly changed:

Backend:

- C#
- .NET 10
- ASP.NET Core
- Entity Framework Core

Database:

- PostgreSQL

Frontend:

- Vue 3
- TypeScript
- Vite
- Tailwind CSS

API:

- REST
- OpenAPI

Testing:

- xUnit
- ASP.NET Core integration testing
- End-to-end testing where appropriate

Infrastructure:

- Docker
- Git

Logging:

- Serilog

AI:

- OpenAI API initially

Do not introduce a new dependency simply because it is convenient.

Prefer the existing platform capabilities when they are sufficient.

---

# 7. Engineering Principles

Prioritize:

1. Correctness
2. Data integrity
3. Security
4. Simplicity
5. Maintainability
6. Testability
7. Observability
8. Performance
9. Developer experience

Avoid unnecessary abstraction.

Do not create abstractions without a clear reason.

Do not create generic repositories merely to hide Entity Framework Core.

Do not create large "God" services.

Do not put business logic into controllers.

Do not put business-critical logic into Vue components.

Do not duplicate business rules across layers.

---

# 8. Domain Rules

Business rules must have a clear home.

Examples:

Inventory rules belong to the inventory domain/application logic.

Sale rules belong to the sales domain/application logic.

Purchase rules belong to purchasing.

Permission rules belong to the authorization system.

Do not allow controllers or frontend components to independently implement business rules.

Use explicit application commands for important state-changing operations.

Examples:

- CreateProduct
- CreateSale
- ReceivePurchase
- AdjustInventory
- CreateExpense
- ApproveReorder

These operations should be explicit, testable, and auditable.

---

# 9. Inventory Integrity

Inventory is critical business data.

Every inventory change must have a clear reason.

Examples:

- Purchase received
- Sale completed
- Manual adjustment
- Return
- Damage
- Transfer

Avoid silently changing inventory quantities.

Prefer recording inventory movements rather than simply overwriting quantities.

Inventory-related operations should account for concurrency and transaction integrity.

A sale that changes inventory should not leave the database in a partially completed state.

---

# 10. Financial Data

Financial information must be treated as authoritative application data.

Do not allow an LLM to invent:

- prices
- totals
- balances
- taxes
- payments
- expenses
- inventory quantities
- financial transactions

Financial calculations must be performed by deterministic application code.

The AI may explain or summarize verified financial data.

---

# 11. AI Architecture

AI is an assistant layer, not the source of truth.

The AI must NOT have unrestricted direct access to the database.

The AI should interact with controlled application tools/services.

Examples:

- GetSalesSummary
- GetLowStockProducts
- GetInventoryForecast
- GetExpenseSummary
- GetOverduePayments
- GetCustomerActivity

The flow should generally be:

User
→ AI
→ Controlled Tool
→ Application Logic
→ Database
→ Verified Result
→ AI
→ User

The AI may:

- explain information
- summarize information
- identify patterns
- generate recommendations
- prepare drafts
- answer natural-language questions

The AI must not independently perform high-impact mutations.

For example:

AI:
"Rice is projected to run out in four days. I recommend ordering 100 units."

Owner:
Approve

Application:
CreatePurchaseOrder(...)

The application executes the operation, not the LLM.

High-impact actions require explicit application commands and appropriate approval.

---

# 12. AI Provider Abstraction

Do not tightly couple the entire application to OpenAI.

The AI implementation should exist behind our own application interfaces.

For example:

IAIService
IAIModelClient
IAITool

The initial implementation may use OpenAI.

The architecture should allow another provider or model to be introduced later without rewriting the entire Business OS.

---

# 13. Multi-Tenancy

The Business OS is intended to become a SaaS product.

Business-owned data must belong to an organization/tenant.

Examples:

- Products
- Customers
- Sales
- Purchases
- Expenses
- Inventory
- Files

The server must enforce tenant isolation.

Never rely solely on the frontend to determine which organization's data a user may access.

Every protected operation must verify authorization and tenant ownership.

Treat tenant isolation as a critical security boundary.

---

# 14. Authentication and Authorization

Use secure authentication and authorization.

The system must support:

- Users
- Organizations
- Roles
- Permissions

Do not assume that authentication means authorization.

A user being logged in does not automatically mean they can perform every operation.

Authorization must be enforced server-side.

Never expose secrets, tokens, passwords, or sensitive configuration in source code.

---

# 15. Database Rules

Use PostgreSQL with Entity Framework Core.

Use migrations for schema changes.

Do not manually modify production database schemas outside the application's migration process unless explicitly required and documented.

Database changes must consider:

- existing data
- migrations
- indexes
- foreign keys
- constraints
- concurrency
- performance
- tenant isolation

Avoid premature optimization.

However, do not ignore obvious database integrity problems.

---

# 16. API Rules

APIs should be:

- predictable
- explicit
- validated
- secure
- documented

Do not expose domain entities directly as API contracts when doing so creates unnecessary coupling.

Use appropriate request and response models.

Return meaningful HTTP status codes.

Do not expose internal exceptions or sensitive database information to clients.

---

# 17. Error Handling

Errors should be:

- predictable
- structured
- useful to developers
- safe for users

Do not expose stack traces, database connection strings, secrets, or internal implementation details through production API responses.

Use centralized error handling where appropriate.

---

# 18. Observability

The system should eventually provide:

- Structured logging
- Correlation IDs
- Health checks
- Error tracking
- Background job visibility
- Automation execution history
- Audit events

Important business actions should be traceable.

For example:

A user approving a reorder should produce an auditable event.

---

# 19. Testing

Important business behavior must have automated tests.

Prioritize tests for:

- Inventory calculations
- Sales
- Purchases
- Stock movements
- Reorder recommendations
- Forecasting
- Permissions
- Tenant isolation
- Important financial calculations

Use unit tests for isolated business rules.

Use integration tests for:

- database behavior
- transactions
- authentication
- authorization
- API workflows

Use end-to-end tests for critical user journeys.

Do not write tests merely to increase coverage numbers.

Tests should protect meaningful behavior.

---

# 20. Development Workflow

Before implementing a significant feature:

1. Understand the requirement.
2. Inspect the existing architecture.
3. Identify affected modules.
4. Identify data changes.
5. Identify security implications.
6. Plan the implementation.
7. Implement the smallest coherent change.
8. Write or update tests.
9. Run the relevant tests.
10. Review the resulting code.
11. Update documentation when necessary.

Do not make large unrelated changes while implementing a feature.

---

# 21. Human Approval Required

Ask for explicit approval before:

- Changing the overall architecture
- Introducing microservices
- Adding major dependencies
- Changing authentication architecture
- Changing tenant isolation architecture
- Making breaking API changes
- Performing major database migrations
- Deleting significant data
- Deploying to production
- Connecting paid external services
- Automatically executing financial transactions
- Changing the fundamental product scope

Do not assume approval.

---

# 22. Git Discipline

Keep commits focused.

Prefer commits such as:

feat: add product domain model

feat: add inventory movement workflow

test: add inventory transaction tests

fix: prevent negative stock

docs: update architecture documentation

Avoid enormous commits containing unrelated changes.

Do not rewrite unrelated code simply because it could be improved.

---

# 23. Definition of Done

A feature is not complete merely because the code compiles.

When appropriate, completion should include:

- Implementation
- Validation
- Business rules
- Authorization
- Error handling
- Tests
- Database migrations
- Logging
- Documentation
- Security review
- No known critical defects

If something intentionally remains incomplete, state it clearly.

---

# 24. Scope Discipline

Do not build features simply because they are technically interesting.

The first objective is a reliable Business OS foundation.

Do not prematurely build:

- Microservices
- Complex autonomous AI agents
- Mobile applications
- Advanced accounting
- Payroll
- Dozens of integrations
- Highly sophisticated forecasting
- Industry-specific modules

Build the core system first.

Then expand based on actual customer needs.

---

# 25. Product Principle

Always remember:

Record activity
→ Understand activity
→ Recommend actions
→ Execute approved actions
→ Automate safe routine operations

The goal is not to give business owners more software to operate.

The goal is to make the business require less manual operation.