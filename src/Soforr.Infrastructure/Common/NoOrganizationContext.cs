using Soforr.Application.Organizations;

namespace Soforr.Infrastructure.Common;

/// <summary>
/// The default <see cref="IOrganizationContext"/>: there is no organization. It keeps the application resolvable
/// before authentication exists and fails closed afterwards if no real context is registered. The Api replaces it
/// with an implementation based on the authenticated user (IDENTITY-001).
/// </summary>
internal sealed class NoOrganizationContext : IOrganizationContext
{
    public Guid? OrganizationId => null;
}
