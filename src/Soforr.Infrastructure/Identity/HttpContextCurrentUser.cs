using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Soforr.Application.Identity;
using Soforr.Application.Organizations;
using Soforr.Domain.Identity;

namespace Soforr.Infrastructure.Identity;

/// <summary>
/// The caller as established by the authentication cookie. Without an authenticated request (anonymous, or code
/// running outside a request) every value is null, and consumers fail closed.
/// </summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser, IOrganizationContext
{
    private ClaimsPrincipal? Principal =>
        accessor.HttpContext?.User is { Identity.IsAuthenticated: true } principal ? principal : null;

    public Guid? UserId => ParseGuid(Principal?.FindFirstValue(ClaimTypes.NameIdentifier));

    public Guid? OrganizationId => ParseGuid(Principal?.FindFirstValue(MembershipClaimsPrincipalFactory.OrganizationClaimType));

    public Role? Role =>
        Enum.TryParse<Role>(Principal?.FindFirstValue(ClaimTypes.Role), ignoreCase: false, out var role) && Enum.IsDefined(role) ? role : null;

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, CultureInfo.InvariantCulture, out var id) && id != Guid.Empty ? id : null;
}
