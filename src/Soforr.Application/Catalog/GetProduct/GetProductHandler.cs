using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;

namespace Soforr.Application.Catalog.GetProduct;

public sealed record GetProductQuery(Guid Id);

public sealed class GetProductHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<GetProductQuery, ProductDto>
{
    public async Task<Result<ProductDto>> HandleAsync(GetProductQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.ProductView) is { } denied)
        {
            return Result.Failure<ProductDto>(denied);
        }

        // Filtered to the caller's organization: another organization's product is not found.
        var product = await db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == query.Id, cancellationToken);

        return product is null
            ? Result.Failure<ProductDto>(ProductErrors.NotFound(query.Id))
            : Result.Success(ProductDto.From(product, user.Has(Permission.ProductViewCost)));
    }
}
