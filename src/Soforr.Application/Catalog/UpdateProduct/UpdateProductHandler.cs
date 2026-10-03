using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Organizations;
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

        var currency = await OrganizationCurrency.ResolveAsync(db, user.OrganizationId, cancellationToken);
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

        // An expected conflict must not leave the tracked product half-changed, so the SKU is checked before the
        // product is touched. A blank or too-long SKU is skipped here: the Domain rejects it below with its own error.
        if (command.Sku?.Trim() is { Length: > 0 and <= Product.MaxSkuLength } sku
            && await ProductDetails.SkuInUseAsync(db, sku, exceptProductId: product.Id, cancellationToken))
        {
            return Result.Failure<ProductDto>(ProductErrors.SkuTaken);
        }

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

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two requests can pass the check above together; the unique index then decides. Only a SKU that is now
            // taken by another product is a conflict; any other save failure is not ours to hide.
            if (!await ProductDetails.SkuInUseAsync(db, product.Sku, exceptProductId: product.Id, cancellationToken))
            {
                throw;
            }

            return Result.Failure<ProductDto>(ProductErrors.SkuTaken);
        }

        return Result.Success(ProductDto.From(product, user.Has(Permission.ProductViewCost)));
    }
}
