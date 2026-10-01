using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Organizations;

namespace Soforr.Application.Organizations.UpdateOrganizationSettings;

/// <summary>Changes an organization's name and time zone. The currency is fixed at creation and cannot be changed here.</summary>
public sealed record UpdateOrganizationSettingsCommand(Guid OrganizationId, string? Name, string? TimeZoneId);

/// <summary>Owner only, and only for the caller's own organization; any other organization is not found (ADR 0002).</summary>
public sealed class UpdateOrganizationSettingsHandler(IAppDbContext db, IClock clock, ICurrentUser user)
    : ICommandHandler<UpdateOrganizationSettingsCommand, OrganizationDto>
{
    public async Task<Result<OrganizationDto>> HandleAsync(UpdateOrganizationSettingsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.OrganizationManageSettings) is { } denied)
        {
            return Result.Failure<OrganizationDto>(denied);
        }

        if (command.OrganizationId != user.OrganizationId)
        {
            return Result.Failure<OrganizationDto>(OrganizationErrors.NotFound(command.OrganizationId));
        }

        var organization = await db.Set<Organization>().SingleOrDefaultAsync(o => o.Id == command.OrganizationId, cancellationToken);
        if (organization is null)
        {
            return Result.Failure<OrganizationDto>(OrganizationErrors.NotFound(command.OrganizationId));
        }

        // The Domain validates everything before changing anything, so a rejected update leaves the entity untouched
        // and nothing is saved.
        var updated = DomainErrors.Run(
            () =>
            {
                organization.UpdateSettings(command.Name, command.TimeZoneId, clock.UtcNow);
                return organization;
            },
            OrganizationErrors.FieldFor);
        if (updated.IsFailure)
        {
            return Result.Failure<OrganizationDto>(updated.Error!);
        }

        await db.SaveChangesAsync(cancellationToken);

        return Result.Success(OrganizationDto.From(organization));
    }
}
