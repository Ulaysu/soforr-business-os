# 0003 — Commands and queries without a mediator library

- Status: **Accepted** (approved by the project owner, 2026-09-30)
- Date: 2026-09-30
- Deciders: project owner

## Context

CLAUDE.md §8 requires explicit, testable, auditable application commands (`CreateSale`, `ReceivePurchase`, ...) and warns against unnecessary abstraction and new dependencies (§6, §7). The team is small and the first slice has roughly 30 handlers. A mediator library (MediatR or similar) adds a dependency, indirection ("who handles this?"), reflection-based dispatch, and in some cases licensing changes, in exchange for pipeline behaviors that we can achieve simply for now.

## Decision

Do **not** add a mediator package. Use explicit handler classes resolved by dependency injection.

### Shape

```csharp
public sealed record CreateProductCommand(string Sku, string Name, decimal Price);

public sealed class CreateProductHandler(IAppDbContext db, ICurrentUser user, IClock clock)
    : ICommandHandler<CreateProductCommand, ProductDto>
{
    public async Task<Result<ProductDto>> HandleAsync(CreateProductCommand command, CancellationToken ct) { ... }
}
```

- Two tiny interfaces, `ICommandHandler<TCommand, TResult>` and `IQueryHandler<TQuery, TResult>`, each with a single `HandleAsync`. They exist so handlers can be registered by convention and, if ever needed, decorated. There is no `IMediator`/`Send`.
- Endpoints inject the specific handler (or its interface) and call it directly. Navigation is "go to definition".
- Registration: a short assembly-scan extension in Application (`AddApplication()`), no scanning library.
- Handlers **do not call other handlers**. Shared logic is a domain service or an internal application service.
- One handler per file group by module: `Application/<Module>/<UseCase>/`.

### Results and errors

- Handlers return `Result<T>` carrying either a value or an `Error(Code, Kind, Message)`; kinds map centrally to HTTP (`Validation`→400, `NotFound`→404, `Forbidden`→403, `Conflict`→409, `RuleViolation`→422). Exceptions are for unexpected failures only.
- `Result`/`Error` types are Application-level, small, hand-written (introduced in ORG-001).

### Validation

Three layers with distinct jobs, no validation library:
1. **API shape** (required, length, range): .NET's built-in minimal-API validation (DataAnnotations) on request models.
2. **Application rules** (uniqueness, existence, permission-dependent rules): explicit checks at the top of the handler, returning `Result` errors.
3. **Domain invariants**: enforced by entity/value-object constructors and methods; they cannot be bypassed.

### Authorization

Each handler begins with an explicit permission check via `ICurrentUser` (see ADR 0002). It is deliberately visible in the handler. The authorization matrix test guards against omissions.

### Transaction boundaries

- **One command = one unit of work = one `SaveChangesAsync`**, which EF Core executes as a single database transaction. Operations that span modules (e.g. `ReceivePurchase` also writing inventory movements) add all changes to the same `IAppDbContext` and save once, so they commit or fail together.
- Explicit `BeginTransaction` is used only where a command must read-then-write with retry semantics (see ADR 0004).
- Handlers never leave partial state: any failure before save leaves the database unchanged.
- Queries use `AsNoTracking`, project to DTOs, never return domain entities and never save.
- Audit events are added to the same context so they commit atomically with the change.

### Idempotency and retries

Handlers that must be retry-safe take a natural or client-provided idempotency key and enforce it with a unique constraint.

## When to reconsider

Adopt a pipeline (own decorators first, a library only if still justified) when a **demonstrated need** appears: for example, three or more cross-cutting concerns are repeated in most handlers and an omission has caused (or a test shows) a defect; or a genuine need for notification fan-out with multiple handlers per event. Record it in a new ADR. Not "because it is popular".

## Consequences

- Slight repetition (permission check, `SaveChanges`) per handler; visible and reviewable.
- No implicit pipeline: logging of command execution is done by a small explicit call or an endpoint filter, decided in FOUNDATION-005.
- Easy to test: instantiate handler with a test `IAppDbContext`, call `HandleAsync`.
- Easy to migrate to a mediator later if truly needed.

## Alternatives considered

- **MediatR (or similar):** pipeline behaviors and decoupling, but a new dependency, indirection and (recently) commercial licensing for some libraries. Rejected for now.
- **Application services with many methods (`ProductService`):** drifts into God services (CLAUDE.md §7). Rejected.
- **FluentValidation:** useful, but a dependency for logic that is a few `if`s per command at our size. Rejected for now.
