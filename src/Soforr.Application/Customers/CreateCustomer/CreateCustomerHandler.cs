using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Customers;

namespace Soforr.Application.Customers.CreateCustomer;

public sealed record CreateCustomerCommand(string? Name, string? ContactDetails, int? CreditTermsDays);

public sealed class CreateCustomerHandler(IAppDbContext db, IClock clock, IIdGenerator ids, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<CreateCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> HandleAsync(CreateCustomerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.CustomerManage) is { } denied)
        {
            return Result.Failure<CustomerDto>(denied);
        }

        var organizationId = user.OrganizationId!.Value;
        var created = DomainErrors.Run(
            () => Customer.Create(ids.NewId(), organizationId, command.Name, command.ContactDetails, command.CreditTermsDays, clock.UtcNow),
            CustomerErrors.FieldFor);
        if (created.IsFailure)
        {
            return Result.Failure<CustomerDto>(created.Error!);
        }

        var customer = created.Value;
        db.Set<Customer>().Add(customer);
        audit.Record(new AuditEntry(organizationId, AuditActions.CustomerCreated, user.UserId, "Customer", customer.Id, "A customer was created."));
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success(CustomerDto.From(customer, includeContactDetails: true));
    }
}
