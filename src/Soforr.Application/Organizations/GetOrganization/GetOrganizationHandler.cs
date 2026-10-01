using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Persistence;
using Soforr.Domain.Organizations;

namespace Soforr.Application.Organizations.GetOrganization;

public sealed record GetOrganizationQuery(Guid OrganizationId);

public sealed class GetOrganizationHandler(IAppDbContext db) : IQueryHandler<GetOrganizationQuery, OrganizationDto>
{
    public async Task<Result<OrganizationDto>> HandleAsync(GetOrganizationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var organization = await db.Set<Organization>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == query.OrganizationId, cancellationToken);

        return organization is null
            ? Result.Failure<OrganizationDto>(OrganizationErrors.NotFound(query.OrganizationId))
            : Result.Success(OrganizationDto.From(organization));
    }
}
