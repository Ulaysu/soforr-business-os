using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Purchasing;

namespace Soforr.Application.Purchasing.ListSuppliers;

/// <summary>Lists the organization's suppliers by name. Inactive suppliers are left out unless <see cref="IncludeInactive"/> is set.</summary>
public sealed record ListSuppliersQuery(bool IncludeInactive, int Page, int PageSize)
{
    public const int DefaultPageSize = PurchasingErrors.DefaultPageSize;
}

public sealed class ListSuppliersHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<ListSuppliersQuery, PagedResult<SupplierDto>>
{
    public async Task<Result<PagedResult<SupplierDto>>> HandleAsync(ListSuppliersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.PurchasingView) is { } denied)
        {
            return Result.Failure<PagedResult<SupplierDto>>(denied);
        }

        if (PurchasingErrors.CheckPaging<SupplierDto>(query.Page, query.PageSize) is { } invalid)
        {
            return invalid;
        }

        var suppliers = db.Set<Supplier>().AsNoTracking().AsQueryable();
        if (!query.IncludeInactive)
        {
            suppliers = suppliers.Where(supplier => supplier.IsActive);
        }

        var total = await suppliers.CountAsync(cancellationToken);
        var skip = (int)((long)(query.Page - 1) * query.PageSize);
        var page = await suppliers.OrderBy(supplier => supplier.Name).ThenBy(supplier => supplier.Id).Skip(skip).Take(query.PageSize).ToListAsync(cancellationToken);

        return Result.Success(new PagedResult<SupplierDto>(page.Select(SupplierDto.From).ToList(), query.Page, query.PageSize, total));
    }
}
