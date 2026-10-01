using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Organizations;

namespace Soforr.Application.Organizations.GetOrganization;

public sealed record GetOrganizationQuery(Guid OrganizationId);

/// <summary>
/// Reads the caller's own organization. Any other organization is not found, never forbidden, so its existence is
/// not revealed (ADR 0002). The general tenant filter arrives with IDENTITY-003.
/// </summary>
public sealed class GetOrganizationHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<GetOrganizationQuery, OrganizationDto>
{
    public async Task<Result<OrganizationDto>> HandleAsync(GetOrganizationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.OrganizationView) is { } denied)
        {
            return Result.Failure<OrganizationDto>(denied);
        }

        if (query.OrganizationId != user.OrganizationId)
        {
            return Result.Failure<OrganizationDto>(OrganizationErrors.NotFound(query.OrganizationId));
        }

        var organization = await db.Set<Organization>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == query.OrganizationId, cancellationToken);

        return organization is null
            ? Result.Failure<OrganizationDto>(OrganizationErrors.NotFound(query.OrganizationId))
            : Result.Success(OrganizationDto.From(organization));
    }
}
