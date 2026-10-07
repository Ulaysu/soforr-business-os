using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Sales;

namespace Soforr.Application.Sales.GetSale;

public sealed record GetSaleQuery(Guid Id);

public sealed class GetSaleHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<GetSaleQuery, SaleDto>
{
    public async Task<Result<SaleDto>> HandleAsync(GetSaleQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.SalesView) is { } denied)
        {
            return Result.Failure<SaleDto>(denied);
        }

        // Filtered to the caller's organization: another organization's sale is not found.
        var sale = await db.Set<Sale>().AsNoTracking().Include(s => s.Lines).SingleOrDefaultAsync(s => s.Id == query.Id, cancellationToken);

        return sale is null
            ? Result.Failure<SaleDto>(SalesErrors.SaleNotFound(query.Id))
            : Result.Success(SaleDto.From(sale, user.Has(Permission.ProductViewCost)));
    }
}
