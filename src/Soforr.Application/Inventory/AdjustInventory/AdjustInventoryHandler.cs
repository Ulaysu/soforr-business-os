using System.Globalization;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Domain.Inventory;

namespace Soforr.Application.Inventory.AdjustInventory;

/// <summary>
/// Changes a product's stock by hand: a positive change adds stock (found, counted up), a negative one removes it
/// (lost, counted down). The reason is always a manual adjustment, so a note explaining it is required.
/// <see cref="AdjustmentId"/> is an optional, client-chosen identifier of this adjustment: sending the same one again
/// (a retry after a timeout, a double click) changes nothing and reports that it was already recorded. Without it a new
/// adjustment is recorded each time.
/// </summary>
public sealed record AdjustInventoryCommand(Guid ProductId, decimal? QuantityChange, string? Note, Guid? AdjustmentId);

/// <summary>The outcome of an adjustment. <see cref="AlreadyRecorded"/> is true when the same adjustment had already been recorded.</summary>
public sealed record AdjustmentResultDto(Guid MovementId, Guid AdjustmentId, decimal NewQuantity, bool AlreadyRecorded);

public sealed class AdjustInventoryHandler(StockMovementRecorder recorder, IAuditLog audit, ICurrentUser user, IIdGenerator ids)
    : ICommandHandler<AdjustInventoryCommand, AdjustmentResultDto>
{
    public async Task<Result<AdjustmentResultDto>> HandleAsync(AdjustInventoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.InventoryAdjust) is { } denied)
        {
            return Result.Failure<AdjustmentResultDto>(denied);
        }

        if (command.QuantityChange is not { } change)
        {
            return Result.Failure<AdjustmentResultDto>(new Error("inventory.quantity_change_required", ErrorKind.Validation, "The quantity change is required.", "quantityChange"));
        }

        if (command.AdjustmentId == Guid.Empty)
        {
            return Result.Failure<AdjustmentResultDto>(new Error("inventory.adjustment_id_invalid", ErrorKind.Validation, "The adjustment id must be a non-empty identifier or absent.", "adjustmentId"));
        }

        // The adjustment is its own source; a client-supplied id makes a retry idempotent.
        var adjustmentId = command.AdjustmentId ?? ids.NewId();
        var organizationId = user.OrganizationId!.Value;

        var recorded = await recorder.RecordAsync(
            new RecordMovementCommand(command.ProductId, change, InventoryMovementReason.ManualAdjustment, InventorySourceType.Adjustment, adjustmentId, user.UserId!.Value, command.Note),
            cancellationToken,
            // Added to the same unit of work as the movement, so both are saved together or neither is.
            staged => audit.Record(new AuditEntry(
                organizationId,
                AuditActions.StockAdjusted,
                user.UserId,
                "InventoryMovement",
                staged.MovementId,
                string.Create(CultureInfo.InvariantCulture, $"Stock adjusted by {change:+0.####;-0.####}."))));

        if (recorded.IsFailure)
        {
            return Result.Failure<AdjustmentResultDto>(WithField(recorded.Error!));
        }

        var value = recorded.Value;
        return Result.Success(new AdjustmentResultDto(value.MovementId, adjustmentId, value.NewQuantity, value.AlreadyRecorded));
    }

    // The recorder reports Domain errors by code; name the request field each one is about.
    private static Error WithField(Error error)
    {
        if (error.Field is not null || error.Kind != ErrorKind.Validation)
        {
            return error;
        }

        var field = error.Code switch
        {
            _ when error.Code.StartsWith("quantity_delta.", StringComparison.Ordinal) => "quantityChange",
            _ when error.Code.StartsWith("inventory_movement.note_", StringComparison.Ordinal) => "note",
            _ => null,
        };

        return field is null ? error : new Error(error.Code, error.Kind, error.Message, field);
    }
}
