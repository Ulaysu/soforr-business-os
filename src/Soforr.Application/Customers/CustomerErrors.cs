using Soforr.Application.Common;

namespace Soforr.Application.Customers;

/// <summary>Errors and field names of the Customers module.</summary>
internal static class CustomerErrors
{
    public const int DefaultPageSize = 25;
    public const int MaxPage = 1_000_000;
    public const int MaxPageSize = 100;
    public const int MaxSearchTextLength = 100;

    public static Error NotFound(Guid id) =>
        new("customer.not_found", ErrorKind.NotFound, $"The customer '{id}' was not found.");

    public static Error Invalid(string code, string field, string message) => new(code, ErrorKind.Validation, message, field);

    /// <summary>Maps a Domain error code raised while building a customer to the request field it is about, or null.</summary>
    public static string? FieldFor(string code) => code switch
    {
        "customer.name_required" or "customer.name_too_long" => "name",
        "customer.contact_details_too_long" => "contactDetails",
        "customer.credit_terms_negative" or "customer.credit_terms_too_long" => "creditTermsDays",
        _ => null,
    };
}
