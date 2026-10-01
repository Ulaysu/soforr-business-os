using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Infrastructure.Identity;

/// <summary>
/// Creates credential records with Identity's own validators and password hasher, but leaves saving to the caller
/// so that the user, the organization and the membership are stored in one transaction.
/// </summary>
internal sealed class UserAccounts(UserManager<ApplicationUser> userManager, AppDbContext db) : IUserAccounts
{
    // Identity does not cap the password length; hashing a very long password is needless work an attacker could force.
    private const int MaxPasswordLength = 128;
    private const int MaxEmailLength = 256;

    public async Task<Result<Guid>> StageNewUserAsync(Guid userId, string? email, string? password, CancellationToken cancellationToken)
    {
        var address = email?.Trim();
        if (string.IsNullOrEmpty(address))
        {
            return Result.Failure<Guid>(IdentityErrors.EmailInvalid("The e-mail address is required."));
        }

        if (address.Length > MaxEmailLength)
        {
            return Result.Failure<Guid>(IdentityErrors.EmailInvalid($"The e-mail address can have at most {MaxEmailLength} characters."));
        }

        if (string.IsNullOrEmpty(password))
        {
            return Result.Failure<Guid>(IdentityErrors.PasswordInvalid("The password is required."));
        }

        if (password.Length > MaxPasswordLength)
        {
            return Result.Failure<Guid>(IdentityErrors.PasswordInvalid($"The password can have at most {MaxPasswordLength} characters."));
        }

        var user = new ApplicationUser
        {
            Id = userId,
            UserName = address,
            Email = address,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            LockoutEnabled = userManager.Options.Lockout.AllowedForNewUsers,
        };
        await userManager.UpdateNormalizedUserNameAsync(user);
        await userManager.UpdateNormalizedEmailAsync(user);

        foreach (var validator in userManager.UserValidators)
        {
            var result = await validator.ValidateAsync(userManager, user);
            if (result.Succeeded)
            {
                continue;
            }

            return result.Errors.Any(error => error.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName))
                ? Result.Failure<Guid>(IdentityErrors.EmailTaken)
                : Result.Failure<Guid>(IdentityErrors.EmailInvalid(Describe(result)));
        }

        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, user, password);
            if (!result.Succeeded)
            {
                return Result.Failure<Guid>(IdentityErrors.PasswordInvalid(Describe(result)));
            }
        }

        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, password);
        db.Set<ApplicationUser>().Add(user);

        return Result.Success(userId);
    }

    public async Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);

        var normalized = userManager.NormalizeEmail(email);

        return await db.Set<ApplicationUser>()
            .AsNoTracking()
            .AnyAsync(user => user.NormalizedEmail == normalized, cancellationToken);
    }

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(error => error.Description));
}
