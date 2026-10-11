using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;

namespace Soforr.Application.Catalog.SearchProducts;

/// <summary>
/// Lists the organization's products, one page at a time. <see cref="Text"/> matches part of the SKU or the name
/// without regard to case. Inactive products are left out unless <see cref="IncludeInactive"/> is set.
/// </summary>
public sealed record SearchProductsQuery(string? Text, bool IncludeInactive, int Page, int PageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 1_000_000;
    public const int MaxTextLength = 100;
}

public sealed class SearchProductsHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<SearchProductsQuery, PagedResult<ProductDto>>
{
    public async Task<Result<PagedResult<ProductDto>>> HandleAsync(SearchProductsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.ProductView) is { } denied)
        {
            return Result.Failure<PagedResult<ProductDto>>(denied);
        }

        // Out-of-range paging is rejected, not silently corrected.
        if (query.Page is < 1 or > SearchProductsQuery.MaxPage)
        {
            return Result.Failure<PagedResult<ProductDto>>(ProductErrors.Required("pagination.page_invalid", "page", $"The page must be between 1 and {SearchProductsQuery.MaxPage}."));
        }

        if (query.PageSize is < 1 or > SearchProductsQuery.MaxPageSize)
        {
            return Result.Failure<PagedResult<ProductDto>>(ProductErrors.Required("pagination.page_size_invalid", "pageSize", $"The page size must be between 1 and {SearchProductsQuery.MaxPageSize}."));
        }

        var text = query.Text?.Trim();
        if (text is { Length: > SearchProductsQuery.MaxTextLength })
        {
            return Result.Failure<PagedResult<ProductDto>>(ProductErrors.Required("product.search_text_too_long", "q", $"The search text can have at most {SearchProductsQuery.MaxTextLength} characters."));
        }

        var products = db.Set<Product>().AsNoTracking().AsQueryable();
        if (!query.IncludeInactive)
        {
            products = products.Where(product => product.IsActive);
        }

        if (!string.IsNullOrEmpty(text))
        {
            products = MatchingText(products, text);
        }

        var total = await products.CountAsync(cancellationToken);
        var includeCost = user.Has(Permission.ProductViewCost);
        var skip = (int)((long)(query.Page - 1) * query.PageSize);

        var page = await products
            .OrderBy(product => product.Name)
            .ThenBy(product => product.Id)
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedResult<ProductDto>(page.Select(product => ProductDto.From(product, includeCost)).ToList(), query.Page, query.PageSize, total));
    }

    [SuppressMessage("Globalization", "CA1304:Specify CultureInfo", Justification = "The query must translate to the database's lower(), the function behind the SKU key (ADR 0006); there is no culture on the database side.")]
    [SuppressMessage("Globalization", "CA1311:Specify a culture or use an invariant version", Justification = "See CA1304.")]
    [SuppressMessage("Globalization", "CA1862:Use the StringComparison method overloads", Justification = "An OrdinalIgnoreCase comparison does not translate to SQL.")]
    internal static IQueryable<Product> MatchingText(IQueryable<Product> products, string text)
    {
        var lowered = ProductSkuKey.Of(text);

        return products.Where(product =>
            EF.Property<string>(product, ProductSkuKey.PropertyName).Contains(lowered)
            || product.Name.ToLower().Contains(lowered));
    }
}
