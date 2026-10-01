using Soforr.Application.Common;

namespace Soforr.Application.Identity;

/// <summary>
/// The credential store port, implemented by Infrastructure on top of the identity system. Credentials (the
/// password hash, lockout state, security stamp) never reach the Domain or the Application.
/// </summary>
public interface IUserAccounts
{
    /// <summary>
    /// Validates the e-mail address and the password against the account rules, hashes the password and adds the
    /// new user to the current unit of work <b>without saving</b>, so the caller can save the user together with the
    /// rest of the registration in one transaction. Fails with a Validation error (field <c>email</c> or
    /// <c>password</c>) or a Conflict when the e-mail address is already registered.
    /// </summary>
    Task<Result<Guid>> StageNewUserAsync(Guid userId, string? email, string? password, CancellationToken cancellationToken);

    /// <summary>Whether an account with this e-mail address exists. Used to classify a failed save as a duplicate.</summary>
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);
}
