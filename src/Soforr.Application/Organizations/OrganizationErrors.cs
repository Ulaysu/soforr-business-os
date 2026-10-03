using Soforr.Application.Common;

namespace Soforr.Application.Organizations;

/// <summary>Errors and field names of the Organization module.</summary>
internal static class OrganizationErrors
{
    public static Error NotFound(Guid id) =>
        new("organization.not_found", ErrorKind.NotFound, $"The organization '{id}' was not found.");

    /// <summary>The caller's own organization could not be resolved (it does not exist, or the request has none).</summary>
    public static Error CurrentNotFound { get; } = new("organization.not_found", ErrorKind.NotFound, "The organization was not found.");

    /// <summary>Maps a Domain error code to the request field it is about, or null when it is not about an input field.</summary>
    public static string? FieldFor(string code) => code switch
    {
        "organization.name_required" or "organization.name_too_long" => "name",
        "organization.timezone_invalid" => "timeZoneId",
        "currency.invalid" => "currency",
        _ => null,
    };
}
