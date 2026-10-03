using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Soforr.Application.Audit;
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
    IAuditLog audit,
    ICurrentUser currentUser,
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

        var wasLocked = await userManager.IsLockedOutAsync(user);
        var check = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!check.Succeeded)
        {
            // The caller always gets the same error. The reason is logged without the e-mail address.
            LogSignInRefused(check.IsLockedOut);

            // Only the moment an account becomes locked is audited, once: a refused attempt is not, so an anonymous
            // caller cannot make the system write unlimited audit rows.
            if (check.IsLockedOut && !wasLocked)
            {
                await AuditAsync(user.Id, AuditActions.AccountLocked, "Account locked after repeated wrong passwords.", cancellationToken);
            }

            return Result.Failure<AccountDto>(IdentityErrors.InvalidCredentials);
        }

        var properties = new AuthenticationProperties { IsPersistent = false };
        properties.Items[SessionPolicy.SessionStartKey] = SessionPolicy.Format(clock.UtcNow);
        await signInManager.SignInWithClaimsAsync(user, properties, []);

        // Signing in is what establishes the organization, so this lookup cannot be filtered by it.
        var membership = await db.Set<Membership>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == user.Id, cancellationToken);

        if (membership is not null)
        {
            audit.Record(new AuditEntry(membership.OrganizationId, AuditActions.SignInSucceeded, user.Id, "User", user.Id));
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(new AccountDto(user.Id, membership?.OrganizationId, membership?.Role));
    }

    public async Task SignOutAsync()
    {
        // Recorded before the cookie is cleared, while the caller is still known. A sign-out without a session has no
        // organization and records nothing.
        if (currentUser is { UserId: { } userId, OrganizationId: { } organizationId })
        {
            audit.Record(new AuditEntry(organizationId, AuditActions.SignedOut, userId, "User", userId));
            await db.SaveChangesAsync();
        }

        await signInManager.SignOutAsync();
    }

    /// <summary>Records an event for a user who has no session yet (the organization comes from the membership) and saves it.</summary>
    private async Task AuditAsync(Guid userId, string action, string description, CancellationToken cancellationToken)
    {
        var membership = await db.Set<Membership>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

        if (membership is not null)
        {
            audit.Record(new AuditEntry(membership.OrganizationId, action, userId, "User", userId, description));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sign-in was refused (locked out: {LockedOut}).")]
    private partial void LogSignInRefused(bool lockedOut);
}
