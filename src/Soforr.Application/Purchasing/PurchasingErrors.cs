using Soforr.Application.Common;
using Soforr.Domain.Common;

namespace Soforr.Application.Purchasing;

/// <summary>Errors and field names of the Purchasing module.</summary>
internal static class PurchasingErrors
{
    public const int MaxLines = 200;
    public const int MaxPage = 1_000_000;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 25;

    public static Error SupplierNotFound(Guid id) =>
        new("supplier.not_found", ErrorKind.NotFound, $"The supplier '{id}' was not found.");

    public static Error SupplierInactive { get; } =
        new("supplier.inactive", ErrorKind.RuleViolation, "The supplier is inactive and cannot be ordered from.", "supplierId");

    public static Error OrderNotFound(Guid id) =>
        new("purchase_order.not_found", ErrorKind.NotFound, $"The purchase order '{id}' was not found.");

    public static Error ProductNotFound(Guid id, string field) =>
        new("product.not_found", ErrorKind.NotFound, $"The product '{id}' was not found.", field);

    public static Error ProductInactive(string field) =>
        new("purchase_order.product_inactive", ErrorKind.RuleViolation, "An inactive product cannot be ordered.", field);

    /// <summary>Another request changed the same order at the same time and the bounded retries ran out.</summary>
    public static Error ConcurrencyConflict { get; } =
        new("purchase_order.concurrency_conflict", ErrorKind.Conflict, "The order was changed by someone else at the same time. Please try again.");

    /// <summary>A receipt id that was already used for a different receipt (another order, other lines or other quantities).</summary>
    public static Error ReceiptIdConflict { get; } =
        new("purchase_order.receipt_id_conflict", ErrorKind.Conflict, "This receipt id was already used for a different receipt. Use a new receipt id, or send exactly the same receipt again.");

    public static Error Invalid(string code, string field, string message) => new(code, ErrorKind.Validation, message, field);

    /// <summary>
    /// A rejected Domain operation. Most are rule violations (the order is not in a state that allows it, or the receipt
    /// would exceed what was ordered); a missing line is not found; the rest are bad input.
    /// </summary>
    public static Error FromDomain(DomainException exception, Func<string, string?>? fieldFor = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var kind = exception.Code switch
        {
            "purchase_order.line_not_found" => ErrorKind.NotFound,
            "purchase_order.not_receivable" or "purchase_order.receipt_exceeds_ordered" or "purchase_order.cannot_place"
                or "purchase_order.cannot_cancel" or "purchase_order.no_lines" or "purchase_order.not_draft" => ErrorKind.RuleViolation,
            _ => ErrorKind.Validation,
        };

        return new Error(exception.Code, kind, exception.Message, fieldFor?.Invoke(exception.Code));
    }

    public static Result<T> Run<T>(Func<T> operation, Func<string, string?>? fieldFor = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return Result.Success(operation());
        }
        catch (DomainException exception)
        {
            return Result.Failure<T>(FromDomain(exception, fieldFor));
        }
    }

    public static Result<PagedResult<T>>? CheckPaging<T>(int page, int pageSize)
    {
        if (page is < 1 or > MaxPage)
        {
            return Result.Failure<PagedResult<T>>(Invalid("pagination.page_invalid", "page", $"The page must be between 1 and {MaxPage}."));
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            return Result.Failure<PagedResult<T>>(Invalid("pagination.page_size_invalid", "pageSize", $"The page size must be between 1 and {MaxPageSize}."));
        }

        return null;
    }
}
