using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Domain.Identity;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Infrastructure.Identity;

/// <summary>The cookie session, on top of Identity's <see cref="SignInManager{TUser}"/> (ADR 0002).</summary>
internal sealed partial class SessionService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    AppDbContext db,
    IClock clock,
    ILogger<SessionService> logger) : ISessionService
{
    // A valid hash of an arbitrary password, verified when the e-mail is unknown so that an unknown address costs
    // about as much time as a wrong password and the response time does not reveal which addresses exist.
    private static readonly string DummyHash = new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "unknown-user-dummy-password");

    public async Task<Result<AccountDto>> SignInAsync(string? email, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return Result.Failure<AccountDto>(IdentityErrors.InvalidCredentials);
        }

        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            _ = userManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), DummyHash, password);
            return Result.Failure<AccountDto>(IdentityErrors.InvalidCredentials);
        }

        var check = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!check.Succeeded)
        {
            // The caller always gets the same error. The reason is logged without the e-mail address.
            LogSignInRefused(check.IsLockedOut);
            return Result.Failure<AccountDto>(IdentityErrors.InvalidCredentials);
        }

        var properties = new AuthenticationProperties { IsPersistent = false };
        properties.Items[SessionPolicy.SessionStartKey] = SessionPolicy.Format(clock.UtcNow);
        await signInManager.SignInWithClaimsAsync(user, properties, []);

        var membership = await db.Set<Membership>()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == user.Id, cancellationToken);

        return Result.Success(new AccountDto(user.Id, membership?.OrganizationId, membership?.Role));
    }

    public Task SignOutAsync() => signInManager.SignOutAsync();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sign-in was refused (locked out: {LockedOut}).")]
    private partial void LogSignInRefused(bool lockedOut);
}
