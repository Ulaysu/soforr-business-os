using Soforr.Application.Common;
using Soforr.Application.Persistence;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;

namespace Soforr.Application.Organizations.CreateOrganization;

/// <summary>Creates an organization. The Domain validates the input; this handler assigns the id and the time.</summary>
public sealed record CreateOrganizationCommand(string? Name, string? Currency, string? TimeZoneId);

public sealed class CreateOrganizationHandler(IAppDbContext db, IClock clock, IIdGenerator ids)
    : ICommandHandler<CreateOrganizationCommand, OrganizationDto>
{
    public async Task<Result<OrganizationDto>> HandleAsync(CreateOrganizationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var created = DomainErrors.Run(
            () => Organization.Create(ids.NewId(), command.Name, Currency.Create(command.Currency), command.TimeZoneId, clock.UtcNow),
            OrganizationErrors.FieldFor);
        if (created.IsFailure)
        {
            return Result.Failure<OrganizationDto>(created.Error!);
        }

        db.Set<Organization>().Add(created.Value);
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success(OrganizationDto.From(created.Value));
    }
}
