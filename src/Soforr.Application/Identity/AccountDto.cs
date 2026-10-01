using Soforr.Domain.Identity;

namespace Soforr.Application.Identity;

/// <summary>The signed-in user as clients see them: who they are, which organization they act for, and their role.</summary>
public sealed record AccountDto(Guid UserId, Guid? OrganizationId, Role? Role);
