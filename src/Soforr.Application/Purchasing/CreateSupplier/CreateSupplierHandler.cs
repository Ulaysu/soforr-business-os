using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Purchasing;

namespace Soforr.Application.Purchasing.CreateSupplier;

public sealed record CreateSupplierCommand(string? Name, string? ContactDetails);

public sealed class CreateSupplierHandler(IAppDbContext db, IClock clock, IIdGenerator ids, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<CreateSupplierCommand, SupplierDto>
{
    public async Task<Result<SupplierDto>> HandleAsync(CreateSupplierCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.PurchasingManage) is { } denied)
        {
            return Result.Failure<SupplierDto>(denied);
        }

        var organizationId = user.OrganizationId!.Value;
        var created = DomainErrors.Run(
            () => Supplier.Create(ids.NewId(), organizationId, command.Name, command.ContactDetails, clock.UtcNow),
            code => code switch
            {
                "supplier.name_required" or "supplier.name_too_long" => "name",
                "supplier.contact_details_too_long" => "contactDetails",
                _ => null,
            });
        if (created.IsFailure)
        {
            return Result.Failure<SupplierDto>(created.Error!);
        }

        var supplier = created.Value;
        db.Set<Supplier>().Add(supplier);
        audit.Record(new AuditEntry(organizationId, AuditActions.SupplierCreated, user.UserId, "Supplier", supplier.Id, "A supplier was created."));
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success(SupplierDto.From(supplier));
    }
}
