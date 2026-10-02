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

        // A product's first stock level is created in memory only: it is added to the unit of work after the movement was
        // applied, so a movement that is refused leaves nothing tracked (a use case may stage several movements and
        // save once, and must be able to drop a refused one without clearing the others).
        var opened = level is null;
        level ??= StockLevel.Open(organizationId, command.ProductId, now);

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

        if (opened)
        {
            db.Set<StockLevel>().Add(level);
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
                        throw new StockLevelRaceException(exception);
                    }

                    throw;
                }

                return Result.Success(new MovementResult(value.MovementId, value.NewQuantity, AlreadyRecorded: false));
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs a stage-and-save attempt up to <see cref="MaxAttempts"/> times. This is the retry of ADR 0004 for stock and
    /// <b>only</b> for stock: it retries a concurrency conflict on a <see cref="StockLevel"/> (after discarding
    /// everything the failed attempt tracked and a short random pause) and the loss of the race to open a product's
    /// first stock level. A concurrency failure that does not involve a stock level (some other entity in the same
    /// unit of work) is not an inventory conflict and is rethrown untouched, as is every other failure
    /// (<see cref="RecordAsync"/> recognizes the duplicate-movement race by what the database now holds, and never
    /// assumes). A stock conflict that outlasts the attempts is returned as <c>inventory.concurrency_conflict</c>.
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
            catch (Exception exception) when (IsStockConflict(exception) && number < MaxAttempts)
            {
                await PauseAsync(cancellationToken);
            }
            catch (Exception exception) when (IsStockConflict(exception))
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

    /// <summary>A concurrency failure on a stock level, or the lost race to open one. Nothing else is an inventory conflict.</summary>
    private static bool IsStockConflict(Exception exception) =>
        exception is StockLevelRaceException
        || (exception is DbUpdateConcurrencyException concurrency && concurrency.Entries.Any(entry => entry.Entity is StockLevel));

    private static Task PauseAsync(CancellationToken cancellationToken) =>
        Task.Delay(RandomNumberGenerator.GetInt32(5, 40), cancellationToken);
}

/// <summary>A movement added to the unit of work (not yet saved).</summary>
public sealed record StagedMovement(Guid MovementId, decimal NewQuantity, bool AlreadyRecorded, bool OpenedLevel);

/// <summary>Thrown inside the recorder when another request opened the product's first stock level first, so the attempt is retried.</summary>
internal sealed class StockLevelRaceException(Exception inner) : Exception("The product's first stock level was opened by a concurrent request.", inner);
