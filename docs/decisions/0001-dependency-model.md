# 0001 — Layer dependency model

- Status: **Proposed** (needs human approval; amends CLAUDE.md §5)
- Date: 2026-09-30
- Deciders: project owner

## Context

Three sources describe the dependency direction and they are not identical:

| Source | Statement |
|---|---|
| `CLAUDE.md` §5 | `Api → Application → Domain`; `Infrastructure → Application → Domain`; Domain depends on nothing. `Api → Infrastructure` is not listed. |
| `CLAUDE.md` §4 | Api is responsible for "Dependency injection composition"; Infrastructure implements interfaces required by Application/Domain. |
| Existing `.csproj` files and `architecture.md` | `Api → Application + Infrastructure`; `Infrastructure → Application + Domain`; `Application → Domain`; Domain → none. |

Two questions follow.

**A. Does Api reference Infrastructure?** Something must register Infrastructure services (DbContext, providers). CLAUDE.md §4 gives that job to Api, so the reference is implied; §5 just does not state it. The `.csproj` files already match the implied reading. This is a wording gap, not a code problem.

**B. How does Application reach the database?** CLAUDE.md forbids "generic repositories merely to hide EF Core" and requires Application to own use cases, yet `Application → Domain` only means Application cannot see `DbContext` (which lives in Infrastructure). Handlers such as `CreateSale` or `SearchProducts` need to query and save. This gap is not visible in the current code (there are no handlers yet) but would surface in ORG-001/FOUNDATION-003 and force an unplanned decision.

## Options for B

1. **Application references the provider-agnostic `Microsoft.EntityFrameworkCore` package** and declares an `IAppDbContext` port (`DbSet<T>`s, `SaveChangesAsync`, transaction helper). Infrastructure's `AppDbContext` implements it; Npgsql, migrations and entity configuration stay in Infrastructure. Handlers use LINQ directly; queries project to DTOs with `AsNoTracking`.
2. **Handlers live in Infrastructure** (or Application handlers depend on a per-aggregate store interface for every operation). Keeps EF out of Application but moves use cases into the persistence layer, or creates one store interface per query, which is the repository pattern under another name.
3. **Narrow per-use-case ports** (`IProductStore.FindBySku`, ...) implemented in Infrastructure. Purist, but produces a large port surface, and read-model queries (reports, searches, forecasts) need bespoke ports each time.

## Decision (recommended)

**A. Ratify `Api → Application + Infrastructure`**, with Infrastructure referenced *only* for composition (`Program.cs` / a composition extension). Endpoint code depends on Application types only.

**B. Option 1.**
- `Soforr.Application` may reference `Microsoft.EntityFrameworkCore` (not Npgsql, not `Microsoft.EntityFrameworkCore.Design`, not ASP.NET Core).
- `IAppDbContext` is the only persistence port; no generic repository or unit-of-work wrappers.
- `Soforr.Domain` references nothing outside itself: no EF Core, no attributes from persistence libraries. Entities are mapped by Fluent API in Infrastructure.
- Reasoning: least code that keeps Domain pure; EF Core is already an approved platform dependency; provider independence for PostgreSQL is preserved because Npgsql stays in Infrastructure.

Resulting rule set:

```text
Domain          → (nothing)
Application     → Domain, Microsoft.EntityFrameworkCore (abstractions use only)
Infrastructure  → Application, Domain, EF Core provider, Npgsql, external SDKs
Api             → Application, Infrastructure (composition only)
Web             → Api over HTTP
```

Enforced by architecture tests (FOUNDATION-006): Domain has no package or project references; Application does not reference Infrastructure, Api, Npgsql or ASP.NET Core; endpoints do not use Infrastructure types.

## Consequences

- `CLAUDE.md` §5 must be amended by the owner (proposed wording below). Per [AGENTS.md](../../AGENTS.md) source-of-truth order, CLAUDE.md outranks ADRs, so an accepted ADR alone does not override it. Agents must not edit CLAUDE.md themselves.
- Handlers are unit-testable only with a real or SQLite/in-memory provider for LINQ semantics; the testing ADR (0005) resolves this by using real PostgreSQL for anything touching queries.
- Application has a compile-time dependency on EF Core, i.e. LINQ-to-EF behaviour can leak into handlers. Accepted trade-off.
- Reversal cost: moderate; `IAppDbContext` can later be replaced by ports without touching Domain.

Proposed CLAUDE.md §5 replacement:

```text
Domain          → nothing outside the Domain layer
Application     → Domain (and provider-agnostic EF Core abstractions via IAppDbContext)
Infrastructure  → Application, Domain
Api             → Application, Infrastructure (Infrastructure only for dependency-injection composition)
```

## Alternatives considered

Options 2 and 3 above. Also: a separate `Soforr.Host` project as composition root so Api need not reference Infrastructure — cleaner in theory, but adds a project for no current benefit. Revisit if a second host (background worker) appears.
