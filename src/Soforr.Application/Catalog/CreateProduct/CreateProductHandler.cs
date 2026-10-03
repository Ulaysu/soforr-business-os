using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Organizations;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;

namespace Soforr.Application.Catalog.CreateProduct;

/// <summary>
/// Creates a product in the caller's organization. The prices are amounts in the organization's currency, which the
/// handler supplies, so a product can never be priced in another currency.
/// </summary>
public sealed record CreateProductCommand(
    string? Sku,
    string? Name,
    string? UnitOfMeasure,
    decimal? SellingPrice,
    decimal? CostPrice,
    decimal? ReorderPoint,
    int? ReorderLeadTimeDays);

public sealed class CreateProductHandler(IAppDbContext db, IClock clock, IIdGenerator ids, ICurrentUser user)
    : ICommandHandler<CreateProductCommand, ProductDto>
{
    public async Task<Result<ProductDto>> HandleAsync(CreateProductCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.ProductManage) is { } denied)
        {
            return Result.Failure<ProductDto>(denied);
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
        var created = DomainErrors.Run(
            () => Product.Create(ids.NewId(), user.OrganizationId!.Value, command.Sku, command.Name, command.UnitOfMeasure, selling, cost, reorder, clock.UtcNow),
            ProductErrors.FieldFor);
        if (created.IsFailure)
        {
            return Result.Failure<ProductDto>(created.Error!);
        }

        var product = created.Value;
        if (await ProductDetails.SkuInUseAsync(db, product.Sku, exceptProductId: null, cancellationToken))
        {
            return Result.Failure<ProductDto>(ProductErrors.SkuTaken);
        }

        db.Set<Product>().Add(product);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two requests can pass the check above together; the unique index then decides. Anything else that
            // failed to save is not ours to hide.
            if (!await ProductDetails.SkuInUseAsync(db, product.Sku, exceptProductId: product.Id, cancellationToken))
            {
                throw;
            }

            return Result.Failure<ProductDto>(ProductErrors.SkuTaken);
        }

        return Result.Success(ProductDto.From(product, user.Has(Permission.ProductViewCost)));
    }
}
