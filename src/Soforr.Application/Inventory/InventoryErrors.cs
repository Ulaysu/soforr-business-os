using Soforr.Application.Common;

namespace Soforr.Application.Inventory;

/// <summary>Errors of the Inventory module.</summary>
public static class InventoryErrors
{
    public const string InsufficientStockCode = "stock_level.insufficient_stock";

    /// <summary>A bounded number of retries ran out because other writers kept changing the same stock (ADR 0004).</summary>
    public static Error ConcurrencyConflict { get; } =
        new("inventory.concurrency_conflict", ErrorKind.Conflict, "The stock was changed by someone else at the same time. Please try again.");

    public static Error ProductNotFound(Guid productId) =>
        new("product.not_found", ErrorKind.NotFound, $"The product '{productId}' was not found.");

    public static Error NoOrganization { get; } =
        new("identity.forbidden", ErrorKind.Forbidden, "You are not allowed to do this.");
}
