using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;

namespace Soforr.Application.Catalog.DeactivateProduct;

/// <summary>Takes a product out of use without deleting it. Deactivating an inactive product changes nothing.</summary>
public sealed record DeactivateProductCommand(Guid Id);

public sealed class DeactivateProductHandler(IAppDbContext db, IClock clock, ICurrentUser user)
    : ICommandHandler<DeactivateProductCommand, ProductDto>
{
    public async Task<Result<ProductDto>> HandleAsync(DeactivateProductCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.ProductManage) is { } denied)
        {
            return Result.Failure<ProductDto>(denied);
        }

        var product = await db.Set<Product>().SingleOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (product is null)
        {
            return Result.Failure<ProductDto>(ProductErrors.NotFound(command.Id));
        }

        product.Deactivate(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success(ProductDto.From(product, user.Has(Permission.ProductViewCost)));
    }
}
