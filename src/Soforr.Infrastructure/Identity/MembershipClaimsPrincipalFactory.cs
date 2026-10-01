using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Soforr.Domain.Identity;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Infrastructure.Identity;

/// <summary>
/// Puts the user's organization and role into the sign-in ticket (ADR 0002). Identity runs this at sign-in and
/// again every time the security stamp is re-validated, so a changed role or a removed membership is picked up
/// within the validation interval. The values come from the stored <see cref="Membership"/>, never from the client.
/// </summary>
internal sealed class MembershipClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityOptions> options,
    AppDbContext db) : UserClaimsPrincipalFactory<ApplicationUser>(userManager, options)
{
    public const string OrganizationClaimType = "soforr.organization";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        // Signing in is what establishes the organization, so this lookup cannot be filtered by it.
        var membership = await db.Set<Membership>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == user.Id);

        if (membership is not null)
        {
            identity.AddClaim(new Claim(OrganizationClaimType, membership.OrganizationId.ToString()));
            identity.AddClaim(new Claim(ClaimTypes.Role, membership.Role.ToString()));
        }

        return identity;
    }
}
