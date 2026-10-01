using Soforr.Application.Common;

namespace Soforr.Application.Identity;

/// <summary>Errors and field names of the Identity module.</summary>
public static class IdentityErrors
{
    public static Error InvalidCredentials { get; } =
        new("identity.invalid_credentials", ErrorKind.Unauthenticated, "The e-mail address or the password is wrong.");

    public static Error NotAuthenticated { get; } =
        new("identity.not_authenticated", ErrorKind.Unauthenticated, "You must sign in to do this.");

    public static Error EmailTaken { get; } =
        new("identity.email_taken", ErrorKind.Conflict, "An account with this e-mail address already exists.", "email");

    public static Error EmailInvalid(string message) => new("identity.email_invalid", ErrorKind.Validation, message, "email");

    public static Error PasswordInvalid(string message) => new("identity.password_invalid", ErrorKind.Validation, message, "password");

    /// <summary>Maps a Domain error code raised while registering to the request field it is about.</summary>
    public static string? RegistrationFieldFor(string code) => code switch
    {
        "organization.name_required" or "organization.name_too_long" => "organizationName",
        "organization.timezone_invalid" => "timeZoneId",
        "currency.invalid" => "currency",
        _ => null,
    };
}
