using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Customers;

namespace Soforr.Application.Customers.UpdateCustomer;

/// <summary>Replaces a customer's name, contact details and credit terms (all three are sent; an absent contact or terms clears it).</summary>
public sealed record UpdateCustomerCommand(Guid Id, string? Name, string? ContactDetails, int? CreditTermsDays);

public sealed class UpdateCustomerHandler(IAppDbContext db, IClock clock, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<UpdateCustomerCommand, CustomerDto>
{
    public async Task<Result<CustomerDto>> HandleAsync(UpdateCustomerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.CustomerManage) is { } denied)
        {
            return Result.Failure<CustomerDto>(denied);
        }

        // The query is filtered to the caller's organization, so another organization's customer is simply not found.
        var customer = await db.Set<Customer>().SingleOrDefaultAsync(c => c.Id == command.Id, cancellationToken);
        if (customer is null)
        {
            return Result.Failure<CustomerDto>(CustomerErrors.NotFound(command.Id));
        }

        // The Domain validates everything before changing anything, so a rejected update leaves the entity untouched.
        var updated = DomainErrors.Run(
            () =>
            {
                customer.Update(command.Name, command.ContactDetails, command.CreditTermsDays, clock.UtcNow);
                return customer;
            },
            CustomerErrors.FieldFor);
        if (updated.IsFailure)
        {
            return Result.Failure<CustomerDto>(updated.Error!);
        }

        audit.Record(new AuditEntry(customer.OrganizationId, AuditActions.CustomerUpdated, user.UserId, "Customer", customer.Id, "A customer was updated."));
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success(CustomerDto.From(customer, includeContactDetails: true));
    }
}
