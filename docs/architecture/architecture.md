# Architecture

Soforr Business OS is a **modular monolith**: one deployable application with strong internal module boundaries. Rules that live in [CLAUDE.md](../../CLAUDE.md) are not repeated at length here; this document is the structural reference.

## Projects

```text
src/
├── Soforr.Api             ASP.NET Core host, HTTP endpoints, auth configuration, composition root
├── Soforr.Application     Use cases (commands/queries), DTOs, validation, ports (interfaces)
├── Soforr.Domain          Entities, value objects, business invariants
├── Soforr.Infrastructure  EF Core, PostgreSQL, file storage, email, AI provider implementations
└── Soforr.Web             Vue 3 + TypeScript + Vite + Tailwind SPA (created in FOUNDATION-007)
```

Test projects live under `tests/` (see [testing.md](../engineering/testing.md)).

## Dependency Direction

```text
Application    → Domain (+ provider-agnostic EF Core abstractions via IAppDbContext)
Infrastructure → Application + Domain
Api            → Application + Infrastructure   (Infrastructure only for DI composition)
Domain         → nothing outside Domain
Web            → Api over HTTP only
```

- No circular references. The Domain has no reference to ASP.NET Core, EF Core, Npgsql, Vue or any AI SDK.
- Application defines the interfaces it needs (`IClock`, `ICurrentUser`, `IAIModelClient`, file storage, ...); Infrastructure implements them.
- [ADR 0001](../decisions/0001-dependency-model.md) (Accepted) defines how Application reaches the database (`IAppDbContext` port).
- Api may reference Infrastructure solely to register services in `Program.cs`. Endpoints depend on Application, never on Infrastructure types.

## Layer Responsibilities

| Layer | Owns | Does not own |
|---|---|---|
| Api | Routing, request/response models, status codes, authN/authZ wiring, middleware (error handling, correlation ID), OpenAPI | Business rules, data access |
| Application | Commands, queries, handlers, orchestration, transactions, per-use-case authorization checks, validation, DTOs | HTTP concerns, SQL |
| Domain | Invariants, entities, value objects (Money, Quantity), domain events where they earn their place | Persistence, framework types |
| Infrastructure | `DbContext`, entity configurations, migrations, adapters for external services | Business decisions |
| Web | Presentation, client state, API calls, input ergonomics | Any rule that must hold for data integrity |

## Prohibited

- **Fat controllers / business logic in endpoints.** Endpoints map a request to a command/query and a result to a response.
- **Business-critical rules only in Vue.** The frontend may mirror validation for UX; the server is authoritative.
- **Generic repositories** wrapping EF Core.
- **Premature microservices.** Introducing them requires human approval.
- **Direct database access from AI.** See [ai-architecture.md](ai-architecture.md).
- **AI providers inside the Domain layer.** Providers sit behind Application interfaces and are implemented in Infrastructure.
- **Exposing domain entities as API contracts.**
- **One module writing another module's tables.** Modules interact through Application-level interfaces (see [modules.md](modules.md)).

## Cross-Cutting Design

- **Tenancy:** every business-owned row carries `OrganizationId`. Tenant scoping is enforced server-side (EF global query filters plus command-level ownership checks), never trusted from the client. See [security.md](../security/security.md).
- **Use cases:** explicit command/query objects and handlers, without a mediator library unless approved as a dependency. Each state-changing command is auditable.
- **Transactions:** a command is one unit of work. A sale that decrements stock commits atomically or not at all. The concurrency mechanism for stock (optimistic token vs. row lock) is decided in INVENTORY-001's design note.
- **Errors:** RFC 9457 `ProblemDetails` from centralized middleware; expected failures map to 4xx, unexpected errors to 500 with no internals leaked.
- **Time and IDs:** `IClock` abstraction; UUID primary keys generated in application code.
- **Money and quantities:** `decimal` with explicit precision in dedicated value objects; currency stored with money. Never floating point.
- **Persistence:** PostgreSQL via EF Core; schema changes by migrations only.

## Persistence Port

Application reaches the database only through `IAppDbContext` (ADR-0001). Its EF Core dependency is deliberately narrow:

- **Allowed in Application:** the provider-agnostic `Microsoft.EntityFrameworkCore` package: `DbSet<TEntity>`, LINQ over entity sets, and the async query extensions (`ToListAsync`, `FirstOrDefaultAsync`, `AnyAsync`, `Include`, ...). `IAppDbContext` exposes `Set<TEntity>()` and `SaveChangesAsync`; nothing provider-specific.
- **Not allowed in Application:** any other `Microsoft.EntityFrameworkCore.*` package (Relational, Design, provider packages), Npgsql, ASP.NET Core. As a consequence, relational-only APIs (raw SQL such as `FromSql`/`ExecuteSql`, `Database.Migrate`, `ExecuteUpdate`/`ExecuteDelete`, `EF.Functions` PostgreSQL methods) do not compile in Application. If a use case needs one, it is hidden behind a purpose-built Application port implemented in Infrastructure, not exposed through `IAppDbContext`.
- **Domain** references nothing; entities are mapped with the Fluent API in Infrastructure.
- **Infrastructure** owns `AppDbContext`, the Npgsql provider, entity configuration and migrations.

Architecture tests enforce this (`DependencyDirectionTests`, `PersistencePortTests`): declared and compiled references of Application, and the assemblies of every type in the `IAppDbContext` signature.

## Modules and Data

Modules are folders/namespaces inside the existing projects: [modules.md](modules.md). Data conventions: [data-model.md](data-model.md).

## Decisions

Significant decisions are recorded as ADRs in [docs/decisions/](../decisions/README.md).
