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
- Infrastructure references the ASP.NET Core shared framework, because ASP.NET Core Identity's sign-in and cookie handling need it (IDENTITY-001). That does not extend to Application or Domain, which stay free of web and Identity types (architecture tests).
- Api may reference Infrastructure solely to register services in `Program.cs`. Endpoints depend on Application, never on Infrastructure types.

## Layer Responsibilities

| Layer | Owns | Does not own |
|---|---|---|
| Api | Routing, request/response models, status codes, authN/authZ wiring, middleware (error handling, correlation ID), OpenAPI | Business rules, data access |
| Application | Commands, queries, handlers, orchestration, transactions, per-use-case authorization checks, validation, DTOs | HTTP concerns, SQL |
| Domain | Invariants, entities, value objects (Money, Quantity), domain events where they earn their place | Persistence, framework types |
| Infrastructure | `DbContext`, entity configurations, migrations, adapters for external services | Business decisions |
| Web | Presentation, client state, API calls, input ergonomics | Any rule that must hold for data integrity |

## Deterministic Intelligence

Sellable V1 includes deterministic operational intelligence ([sellable-v1.md](../product/sellable-v1.md)). **The existing architecture supports it cleanly; no new project, layer or framework is introduced.** It is an `Intelligence` module inside `Soforr.Application` (see [modules.md](modules.md#intelligence)), built with the same handlers, ports and tenant filters as every other module.

```text
Authoritative business data  (products, movements, purchases, sales, customers, payments)
        ↓  read through IAppDbContext (tenant-filtered)
Gathering                    (a query handler loads exactly the records a rule needs)
        ↓
Pure calculation / rule      (no I/O: typed input and explicit thresholds → insights)
        ↓
Insight                      (type, severity, subject, named facts, reason, recommended action)
        ↓
API / UI                     (presents insights; contains no rule)
```

- **Rules are pure.** A rule or calculation is a function of a typed input snapshot, explicit thresholds and the clock (passed in, never read ambiently). It touches no database and no network, so it is tested with fixtures and a fixed clock, and the same data and clock always give the same result.
- **Gathering is separate from judging.** Query handlers (ADR 0003 conventions: permission check first, tenant filter, `AsNoTracking`, no saving) load the data and call the rules. The Domain keeps entity invariants; metrics and rules live in the Intelligence module because they read across modules.
- **Every insight is traceable.** It carries the named facts it was computed from and a plain reason; an insight that cannot show its facts is not an insight.
- **Derived, never authoritative.** Insights are computed from authoritative data (computed on request by default) and are not the record of what happened. If a later item materializes insights, only their state (for example dismissed) is stored, never the facts as a replacement for the records, and that choice is recorded in an ADR.
- **Cost-derived insights** (margins, purchase cost) require `ProductViewCost`; every insight is tenant-scoped and permission-checked like any other query.
- **Transactions snapshot product data.** Sale and purchase lines record the name, SKU, price, cost and currency at the time, so intelligence reads history that cannot change when a product is edited.
- **No platform.** No LLM, agent, vector store, embedding pipeline or forecasting infrastructure. The later layers (statistical, AI-assisted, automation) add to this module and call these same services as their controlled tools ([ai-architecture.md](ai-architecture.md)); nothing here has to be rewritten for them.

A major architectural change would be proposed, not made silently. None is needed for V1 intelligence.

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
- **Errors:** RFC 9457 `ProblemDetails`. Unexpected errors return a generic 500 with no internals leaked (FOUNDATION-005). Expected failures returned by handlers map to 4xx through one central `Result`-to-HTTP mapping (`ResultHttpExtensions`) that adds a stable error `code`; validation failures also list the problem per field (ORG-003).
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
- `Result<T>` carries a value or an `Error(Code, Kind, Message, Field?)` for expected failures. `ErrorKind` maps to HTTP in one place, `Soforr.Api.Http.ResultHttpExtensions`: Validation 400 (an RFC 9457 validation problem with `errors` per field), NotFound 404, Forbidden 403, Conflict 409, RuleViolation 422; every problem carries `code` and `correlationId`.
- Domain invariants are enforced by Domain constructors and methods, which throw `DomainException` with a stable `Code`. Handlers call the Domain through `DomainErrors.Run`, the one place that translates it to a Validation `Error` (with the field it is about, supplied by the module) instead of letting it escape.
- The Domain never reads a clock or creates ids: time and ids are passed in. Handlers get them from `IClock` and `IIdGenerator` (implemented in Infrastructure), which keeps behavior deterministic in tests.

## Connectivity resilience (proposed, ADR 0007)

V1 critical workflows continue during network outages. This is a client-side capability around the existing server, not a second backend:

```text
Vue UI → client application service → local repository (IndexedDB) + outbox → sync engine → API client → existing API → existing commands → PostgreSQL
```

- The server and PostgreSQL remain the only source of truth. The browser keeps a bounded operational copy (products, customers, recent sales) and an outbox of pending commands.
- The client queues the same commands the online path sends (`CreateSale`, `RecordPayment`); it does not re-implement stock, credit, payment or pricing rules. Replays use the existing client-chosen ids and strict repeat comparison for idempotency.
- Server-side additions are small and additive (event time of a sale, the catalog price the cashier saw, the server's received time, a documented error taxonomy). Insufficient stock, a changed price, a removed product or a changed permission surface as conflicts to resolve, never as silent changes.
- A service worker caches only the application shell. IndexedDB holds business data; the outbox holds pending mutations; the sync engine owns retries, ordering, state and failure classification.
- Security: ADR 0002 is unchanged (cookie session, no stored credentials). Offline use is bounded to a window after the last online validation; local data is per user and organization; the server re-checks tenant, permission and rules on every replay.

Status: Proposed. The web application does not exist yet; the browser part follows FOUNDATION-007 and the online screens.

## Modules and Data

Modules are folders/namespaces inside the existing projects: [modules.md](modules.md). Data conventions: [data-model.md](data-model.md).

## Decisions

Significant decisions are recorded as ADRs in [docs/decisions/](../decisions/README.md).
