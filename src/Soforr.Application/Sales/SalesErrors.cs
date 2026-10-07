using Soforr.Application.Common;
using Soforr.Domain.Common;

namespace Soforr.Application.Sales;

/// <summary>Errors and field names of the Sales module.</summary>
internal static class SalesErrors
{
    public const int MaxLines = 200;
    public const int MaxPage = 1_000_000;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 25;

    public static Error SaleNotFound(Guid id) =>
        new("sale.not_found", ErrorKind.NotFound, $"The sale '{id}' was not found.");

    public static Error ProductNotFound(Guid id, string field) =>
        new("product.not_found", ErrorKind.NotFound, $"The product '{id}' was not found.", field);

    public static Error ProductInactive(string field) =>
        new("sale.product_inactive", ErrorKind.RuleViolation, "An inactive product cannot be sold.", field);

    /// <summary>A sale id that was already used for a different sale (other products or other quantities).</summary>
    public static Error SaleIdConflict { get; } =
        new("sale.id_conflict", ErrorKind.Conflict, "This sale id was already used for a different sale. Use a new sale id, or send exactly the same sale again.");

    /// <summary>Another request changed the same sale or the same stock at the same time and the bounded retries ran out.</summary>
    public static Error ConcurrencyConflict { get; } =
        new("sale.concurrency_conflict", ErrorKind.Conflict, "The sale was changed by someone else at the same time. Please try again.");

    public static Error Invalid(string code, string field, string message) => new(code, ErrorKind.Validation, message, field);

    /// <summary>
    /// A rejected Domain operation. A sale that cannot be voided in its state is a rule violation; the rest are bad input.
    /// </summary>
    public static Error FromDomain(DomainException exception, Func<string, string?>? fieldFor = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var kind = exception.Code is "sale.not_voidable" ? ErrorKind.RuleViolation : ErrorKind.Validation;

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
