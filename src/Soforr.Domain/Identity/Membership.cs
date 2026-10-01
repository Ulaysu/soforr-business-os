namespace Soforr.Domain.Identity;

/// <summary>
/// Places a user in an organization with a role. In the MVP a user belongs to exactly one organization (ADR 0002),
/// which persistence enforces with a unique index on <see cref="UserId"/>. The user's credentials live in the
/// identity system (Infrastructure); the Domain only knows the user's identifier.
/// </summary>
public sealed class Membership
{
    // Required by the persistence layer for materialization; not part of the domain API.
    private Membership()
    {
    }

    private Membership(Guid id, Guid userId, Guid organizationId, Role role, DateTimeOffset createdAtUtc)
    {
        Id = id;
        UserId = userId;
        OrganizationId = organizationId;
        Role = role;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Role Role { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <exception cref="Soforr.Domain.Common.DomainException">An invariant is violated.</exception>
    public static Membership Create(Guid id, Guid userId, Guid organizationId, Role role, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new Common.DomainException("membership.id_required", "A membership must have an identifier.");
        }

        if (userId == Guid.Empty)
        {
            throw new Common.DomainException("membership.user_required", "A membership must belong to a user.");
        }

        if (organizationId == Guid.Empty)
        {
            throw new Common.DomainException("membership.organization_required", "A membership must belong to an organization.");
        }

        if (!Enum.IsDefined(role))
        {
            throw new Common.DomainException("membership.role_invalid", "The role is not a known role.");
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new Common.DomainException("membership.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return new Membership(id, userId, organizationId, role, createdAtUtc);
    }
}
