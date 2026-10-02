using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;

namespace Soforr.Application.Catalog.UpdateProduct;

/// <summary>Replaces a product's details. The currency is the organization's and cannot be chosen.</summary>
public sealed record UpdateProductCommand(
    Guid Id,
    string? Sku,
    string? Name,
    string? UnitOfMeasure,
    decimal? SellingPrice,
    decimal? CostPrice,
    decimal? ReorderPoint,
    int? ReorderLeadTimeDays);

public sealed class UpdateProductHandler(IAppDbContext db, IClock clock, ICurrentUser user)
    : ICommandHandler<UpdateProductCommand, ProductDto>
{
    public async Task<Result<ProductDto>> HandleAsync(UpdateProductCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.ProductManage) is { } denied)
        {
            return Result.Failure<ProductDto>(denied);
        }

        // The query is filtered to the caller's organization, so another organization's product is simply not found.
        var product = await db.Set<Product>().SingleOrDefaultAsync(p => p.Id == command.Id, cancellationToken);
        if (product is null)
        {
            return Result.Failure<ProductDto>(ProductErrors.NotFound(command.Id));
        }

        var currency = await ProductDetails.OrganizationCurrencyAsync(db, user.OrganizationId, cancellationToken);
        if (currency.IsFailure)
        {
            return Result.Failure<ProductDto>(currency.Error!);
        }

        var details = new ProductDetails(command.Sku, command.Name, command.UnitOfMeasure, command.SellingPrice, command.CostPrice, command.ReorderPoint, command.ReorderLeadTimeDays);
        var built = details.Build(currency.Value);
        if (built.IsFailure)
        {
            return Result.Failure<ProductDto>(built.Error!);
        }

        var (selling, cost, reorder) = built.Value;

        // The Domain validates everything before changing anything, so a rejected update leaves the entity untouched.
        var updated = DomainErrors.Run(
            () =>
            {
                product.Update(command.Sku, command.Name, command.UnitOfMeasure, selling, cost, reorder, clock.UtcNow);
                return product;
            },
            ProductErrors.FieldFor);
        if (updated.IsFailure)
        {
            return Result.Failure<ProductDto>(updated.Error!);
        }

        if (await ProductDetails.SkuInUseAsync(db, product.Sku, exceptProductId: product.Id, cancellationToken))
        {
            return Result.Failure<ProductDto>(ProductErrors.SkuTaken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (!await ProductDetails.SkuInUseAsync(db, product.Sku, exceptProductId: product.Id, cancellationToken))
            {
                throw;
            }

            return Result.Failure<ProductDto>(ProductErrors.SkuTaken);
        }

        return Result.Success(ProductDto.From(product, user.Has(Permission.ProductViewCost)));
    }
}
