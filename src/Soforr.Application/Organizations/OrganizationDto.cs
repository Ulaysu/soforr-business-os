using Soforr.Domain.Organizations;

namespace Soforr.Application.Organizations;

/// <summary>An organization as it is returned to callers. The Domain entity is never exposed.</summary>
public sealed record OrganizationDto(
    Guid Id,
    string Name,
    string Currency,
    string TimeZoneId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    internal static OrganizationDto From(Organization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);

        return new OrganizationDto(
            organization.Id,
            organization.Name,
            organization.Currency.Code,
            organization.TimeZoneId,
            organization.CreatedAtUtc,
            organization.UpdatedAtUtc);
    }
}
