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
- **Errors:** RFC 9457 `ProblemDetails` from centralized middleware. Unexpected errors return a generic 500 with no internals leaked (implemented, FOUNDATION-005). Expected failures returned by handlers map to 4xx through a central `Result`-to-HTTP mapping with a stable error `code` (ADR-0003); that mapping is planned with the first endpoints (ORG-003) and is not implemented yet.
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

## Module Layout and Handler Pattern

Modules are folders and namespaces inside the existing projects (see [modules.md](modules.md)); each layer uses the same convention so later modules copy it.

```text
src/Soforr.Domain/
  Common/                shared kernel: DomainException, Currency, Money, Quantity
  <Module>/              entities and value objects of one module (e.g. Organizations/Organization.cs)
src/Soforr.Application/
  Common/                Result<T>, Error, ErrorKind, ICommandHandler, IQueryHandler, handler registration, IClock, IIdGenerator
  Persistence/           IAppDbContext
  <Module>/<UseCase>/    one command or query with its handler, validation and DTOs
src/Soforr.Infrastructure/
  Common/                SystemClock, GuidV7IdGenerator
  Persistence/           AppDbContext, entity configurations, migrations
  Configuration/         validated options helpers
src/Soforr.Api/
  Hosting/, Observability/   cross-cutting HTTP concerns; endpoints are grouped per module
```

The handler pattern is defined by [ADR 0003](../decisions/0003-commands-and-queries-without-mediator.md) and implemented in `Soforr.Application.Common`:

- `ICommandHandler<TCommand, TResult>` and `IQueryHandler<TQuery, TResult>` each have one method returning `Task<Result<TResult>>`. There is no mediator: endpoints call the handler directly.
- `AddApplication()` scans the Application assembly and registers each handler (scoped) as its concrete type and its interface. Two handlers for the same command or query fail at startup.
- `Result<T>` carries a value or an `Error(Code, Kind, Message)` for expected failures. The mapping from `ErrorKind` to HTTP is central and arrives with the first endpoints (ORG-003).
- Domain invariants are enforced by Domain constructors and methods, which throw `DomainException` with a stable `Code`. Handlers translate it to an `Error` rather than letting it escape; that translation is written with the first handlers (ORG-003), once, in the Application layer.
- The Domain never reads a clock or creates ids: time and ids are passed in. Handlers get them from `IClock` and `IIdGenerator` (implemented in Infrastructure), which keeps behavior deterministic in tests.

## Modules and Data

Modules are folders/namespaces inside the existing projects: [modules.md](modules.md). Data conventions: [data-model.md](data-model.md).

## Decisions

Significant decisions are recorded as ADRs in [docs/decisions/](../decisions/README.md).
