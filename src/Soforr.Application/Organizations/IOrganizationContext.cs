namespace Soforr.Application.Organizations;

/// <summary>
/// The organization (tenant) the current request acts for. It is derived on the server from the authenticated
/// user's membership and is the only source of tenant identity for Application and Infrastructure; it is never read
/// from a request body, route or query string (ADR 0002).
/// <see cref="OrganizationId"/> is null when there is no authenticated organization (for example an anonymous
/// request or a background job without one). Consumers must fail closed: a null organization grants access to no
/// tenant data.
/// The implementation that reads the authenticated principal arrives with IDENTITY-001, and the query filters that
/// use this context with IDENTITY-003.
/// </summary>
public interface IOrganizationContext
{
    Guid? OrganizationId { get; }
}
