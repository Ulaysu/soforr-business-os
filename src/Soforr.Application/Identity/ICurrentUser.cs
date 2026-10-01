using Soforr.Domain.Identity;

namespace Soforr.Application.Identity;

/// <summary>
/// The authenticated caller of the current request, as established on the server from the session (ADR 0002).
/// All members are null when the caller is not authenticated; consumers must fail closed.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    Guid? OrganizationId { get; }

    Role? Role { get; }
}
