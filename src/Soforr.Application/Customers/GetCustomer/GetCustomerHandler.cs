using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Customers;

namespace Soforr.Application.Customers.GetCustomer;

public sealed record GetCustomerQuery(Guid Id);

public sealed class GetCustomerHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<GetCustomerQuery, CustomerDto>
{
    public async Task<Result<CustomerDto>> HandleAsync(GetCustomerQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.CustomerView) is { } denied)
        {
            return Result.Failure<CustomerDto>(denied);
        }

        // Filtered to the caller's organization: another organization's customer is not found.
        var customer = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == query.Id, cancellationToken);

        return customer is null
            ? Result.Failure<CustomerDto>(CustomerErrors.NotFound(query.Id))
            : Result.Success(CustomerDto.From(customer, user.Has(Permission.CustomerManage)));
    }
}
