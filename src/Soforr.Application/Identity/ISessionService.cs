using Soforr.Application.Common;

namespace Soforr.Application.Identity;

/// <summary>
/// Starts and ends the cookie session of the current request (ADR 0002). Implemented by Infrastructure; the
/// cookie, its lifetime and its validation are configured there.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Verifies the credentials and starts a session. Every failure (unknown e-mail, wrong password, locked-out
    /// account) is the same <c>identity.invalid_credentials</c> error so the response does not reveal which.
    /// </summary>
    Task<Result<AccountDto>> SignInAsync(string? email, string? password, CancellationToken cancellationToken);

    Task SignOutAsync();
}
