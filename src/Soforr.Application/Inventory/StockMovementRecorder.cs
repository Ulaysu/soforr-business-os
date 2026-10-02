using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Inventory;

namespace Soforr.Application.Inventory;

/// <summary>
/// One stock movement to record. The source (<see cref="SourceType"/> and <see cref="SourceId"/>) with the product and
/// the reason is its idempotency key: recording the same one again changes nothing (ADR 0004).
/// </summary>
public sealed record RecordMovementCommand(
    Guid ProductId,
    decimal Delta,
    InventoryMovementReason Reason,
    InventorySourceType SourceType,
    Guid SourceId,
    Guid ActorId,
    string? Note);

/// <summary>The outcome of recording a movement. <see cref="AlreadyRecorded"/> is true when the same movement already existed and nothing changed.</summary>
public sealed record MovementResult(Guid MovementId, decimal NewQuantity, bool AlreadyRecorded);

/// <summary>
/// The internal service that changes stock (ADR 0004): it writes the movement and updates the product's stock level
/// together, in one transaction. It is not a use case with its own permission; the Purchasing, Sales and adjustment
/// use cases that call it check the caller's permission first. It does refuse to act without an organization and only
/// ever sees the current organization's products and stock (the tenant filter).
/// <para>
/// <see cref="StageAsync"/> adds the change to the current unit of work without saving, so a use case that changes
/// several products (a sale with many lines) can stage all of them and save once. <see cref="RecordAsync"/> stages and
/// saves one movement with the bounded retry the ADR requires. A caller that saves itself must wrap the whole
/// stage-and-save in <see cref="ExecuteWithRetryAsync{T}"/>.
/// </para>
/// </summary>
public sealed class StockMovementRecorder(IAppDbContext db, ICurrentUser user, IClock clock, IIdGenerator ids)
{
    /// <summary>Attempts before a concurrency conflict is returned to the caller (ADR 0004).</summary>
    public const int MaxAttempts = 3;

    /// <summary>Adds the movement and the stock change to the current unit of work without saving.</summary>
    public async Task<Result<StagedMovement>> StageAsync(RecordMovementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.OrganizationId is not { } organizationId)
        {
            return Result.Failure<StagedMovement>(InventoryErrors.NoOrganization);
        }

        // Filtered to the current organization: another organization's product is simply not found.
        if (!await db.Set<Product>().AnyAsync(product => product.Id == command.ProductId, cancellationToken))
        {
            return Result.Failure<StagedMovement>(InventoryErrors.ProductNotFound(command.ProductId));
        }

        var existing = await FindExistingAsync(command, cancellationToken);
        var level = await db.Set<StockLevel>().SingleOrDefaultAsync(candidate => candidate.ProductId == command.ProductId, cancellationToken);
        if (existing is not null)
        {
            return Result.Success(new StagedMovement(existing.Id, level?.Quantity.Value ?? 0m, AlreadyRecorded: true, OpenedLevel: false));
        }

        var now = clock.UtcNow;
        var movement = DomainErrors.Run(
            () => InventoryMovement.Record(
                ids.NewId(),
                organizationId,
                command.ProductId,
                QuantityDelta.Create(command.Delta),
                command.Reason,
                command.SourceType,
                command.SourceId,
                command.ActorId,
                command.Note,
                now));
        if (movement.IsFailure)
        {
            return Result.Failure<StagedMovement>(movement.Error!);
        }

        var opened = level is null;
        if (level is null)
        {
            level = StockLevel.Open(organizationId, command.ProductId, now);
            db.Set<StockLevel>().Add(level);
        }

        // Running out of stock is a business rule, not bad input: it is returned as a rule violation (422).
        try
        {
            level.Apply(movement.Value);
        }
        catch (DomainException exception) when (exception.Code == InventoryErrors.InsufficientStockCode)
        {
            return Result.Failure<StagedMovement>(new Error(exception.Code, ErrorKind.RuleViolation, exception.Message));
        }
        catch (DomainException exception)
        {
            return Result.Failure<StagedMovement>(new Error(exception.Code, ErrorKind.Validation, exception.Message));
        }

        db.Set<InventoryMovement>().Add(movement.Value);

        return Result.Success(new StagedMovement(movement.Value.Id, level.Quantity.Value, AlreadyRecorded: false, OpenedLevel: opened));
    }

    /// <summary>
    /// Records one movement and saves, retrying a bounded number of times when another writer changed the same stock
    /// at the same time. Every attempt starts from a clean unit of work and re-validates, so stock that has meanwhile
    /// run out is reported as the business error it is, not as a conflict.
    /// </summary>
    public Task<Result<MovementResult>> RecordAsync(RecordMovementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ExecuteWithRetryAsync(
            async () =>
            {
                var staged = await StageAsync(command, cancellationToken);
                if (staged.IsFailure)
                {
                    return Result.Failure<MovementResult>(staged.Error!);
                }

                var value = staged.Value;
                if (value.AlreadyRecorded)
                {
                    return Result.Success(new MovementResult(value.MovementId, value.NewQuantity, AlreadyRecorded: true));
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException)
                {
                    // Recognize what the database now holds instead of assuming: a concurrent request may have recorded
                    // this same movement (the idempotency index refused ours) or opened the product's first stock level.
                    if (await FindExistingAsync(command, cancellationToken) is { } recorded)
                    {
                        var current = await db.Set<StockLevel>().AsNoTracking().SingleOrDefaultAsync(level => level.ProductId == command.ProductId, cancellationToken);
                        return Result.Success(new MovementResult(recorded.Id, current?.Quantity.Value ?? 0m, AlreadyRecorded: true));
                    }

                    if (value.OpenedLevel && await db.Set<StockLevel>().AnyAsync(level => level.ProductId == command.ProductId, cancellationToken))
                    {
                        throw new DbUpdateConcurrencyException("The product's first stock level was opened by a concurrent request.", exception);
                    }

                    throw;
                }

                return Result.Success(new MovementResult(value.MovementId, value.NewQuantity, AlreadyRecorded: false));
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs a stage-and-save attempt up to <see cref="MaxAttempts"/> times. Only a concurrency conflict on a stock level
    /// is retried (after discarding everything the failed attempt tracked and a short random pause); a conflict that
    /// outlasts the attempts is returned as <c>inventory.concurrency_conflict</c>. Any other failure is not ours to
    /// hide and is rethrown (<see cref="RecordAsync"/> recognizes the two other known races by what the database now
    /// holds, and never assumes).
    /// </summary>
    public async Task<Result<T>> ExecuteWithRetryAsync<T>(Func<Task<Result<T>>> attempt, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(attempt);

        for (var number = 1; ; number++)
        {
            db.ClearChangeTracker();

            try
            {
                return await attempt();
            }
            catch (DbUpdateConcurrencyException) when (number < MaxAttempts)
            {
                await PauseAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ClearChangeTracker();
                return Result.Failure<T>(InventoryErrors.ConcurrencyConflict);
            }
        }
    }

    private async Task<InventoryMovement?> FindExistingAsync(RecordMovementCommand command, CancellationToken cancellationToken) =>
        await db.Set<InventoryMovement>().AsNoTracking().SingleOrDefaultAsync(
            movement => movement.ProductId == command.ProductId
                && movement.SourceType == command.SourceType
                && movement.SourceId == command.SourceId
                && movement.Reason == command.Reason,
            cancellationToken);

    private static Task PauseAsync(CancellationToken cancellationToken) =>
        Task.Delay(RandomNumberGenerator.GetInt32(5, 40), cancellationToken);
}

/// <summary>A movement added to the unit of work (not yet saved).</summary>
public sealed record StagedMovement(Guid MovementId, decimal NewQuantity, bool AlreadyRecorded, bool OpenedLevel);
