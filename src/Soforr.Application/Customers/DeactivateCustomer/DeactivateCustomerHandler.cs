using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Customers;

namespace Soforr.Application.Customers.DeactivateCustomer;

public sealed record DeactivateCustomerCommand(Guid Id);

/// <summary>Takes a customer out of use. Customers are never deleted, so past sales keep their customer. Deactivating an inactive customer changes nothing (and is not audited again).</summary>
public sealed class DeactivateCustomerHandler(IAppDbContext db, IClock clock, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<DeactivateCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> HandleAsync(DeactivateCustomerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.CustomerManage) is { } denied)
        {
            return Result.Failure<CustomerDto>(denied);
        }

        var customer = await db.Set<Customer>().SingleOrDefaultAsync(c => c.Id == command.Id, cancellationToken);
        if (customer is null)
        {
            return Result.Failure<CustomerDto>(CustomerErrors.NotFound(command.Id));
        }

        if (customer.IsActive)
        {
            customer.Deactivate(clock.UtcNow);
            audit.Record(new AuditEntry(customer.OrganizationId, AuditActions.CustomerDeactivated, user.UserId, "Customer", customer.Id, "A customer was deactivated."));
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(CustomerDto.From(customer, includeContactDetails: true));
    }
}
