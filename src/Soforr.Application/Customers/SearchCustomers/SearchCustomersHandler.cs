using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Customers;

namespace Soforr.Application.Customers.SearchCustomers;

/// <summary>
/// Lists the organization's customers by name, one page at a time. <see cref="Text"/> matches part of the name without regard
/// to case (contact details are never searched: they are personal data). Inactive customers are left out unless
/// <see cref="IncludeInactive"/> is set.
/// </summary>
public sealed record SearchCustomersQuery(string? Text, bool IncludeInactive, int Page, int PageSize)
{
    public const int DefaultPageSize = CustomerErrors.DefaultPageSize;
}

public sealed class SearchCustomersHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<SearchCustomersQuery, PagedResult<CustomerDto>>
{
    public async Task<Result<PagedResult<CustomerDto>>> HandleAsync(SearchCustomersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.CustomerView) is { } denied)
        {
            return Result.Failure<PagedResult<CustomerDto>>(denied);
        }

        // Out-of-range paging is rejected, not silently corrected.
        if (query.Page is < 1 or > CustomerErrors.MaxPage)
        {
            return Fail(CustomerErrors.Invalid("pagination.page_invalid", "page", $"The page must be between 1 and {CustomerErrors.MaxPage}."));
        }

        if (query.PageSize is < 1 or > CustomerErrors.MaxPageSize)
        {
            return Fail(CustomerErrors.Invalid("pagination.page_size_invalid", "pageSize", $"The page size must be between 1 and {CustomerErrors.MaxPageSize}."));
        }

        var text = query.Text?.Trim();
        if (text is { Length: > CustomerErrors.MaxSearchTextLength })
        {
            return Fail(CustomerErrors.Invalid("customer.search_text_too_long", "q", $"The search text can have at most {CustomerErrors.MaxSearchTextLength} characters."));
        }

        var customers = db.Set<Customer>().AsNoTracking().AsQueryable();
        if (!query.IncludeInactive)
        {
            customers = customers.Where(customer => customer.IsActive);
        }

        if (!string.IsNullOrEmpty(text))
        {
            customers = MatchingName(customers, text);
        }

        var total = await customers.CountAsync(cancellationToken);
        var includeContactDetails = user.Has(Permission.CustomerManage);
        var skip = (int)((long)(query.Page - 1) * query.PageSize);

        var page = await customers
            .OrderBy(customer => customer.Name)
            .ThenBy(customer => customer.Id)
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedResult<CustomerDto>(page.Select(customer => CustomerDto.From(customer, includeContactDetails)).ToList(), query.Page, query.PageSize, total));
    }

    [SuppressMessage("Globalization", "CA1304:Specify CultureInfo", Justification = "The query must translate to the database's lower(); there is no culture on the database side.")]
    [SuppressMessage("Globalization", "CA1311:Specify a culture or use an invariant version", Justification = "See CA1304.")]
    [SuppressMessage("Globalization", "CA1862:Use the StringComparison method overloads", Justification = "An OrdinalIgnoreCase comparison does not translate to SQL.")]
    private static IQueryable<Customer> MatchingName(IQueryable<Customer> customers, string text)
    {
        var lowered = text.ToLowerInvariant();

        return customers.Where(customer => customer.Name.ToLower().Contains(lowered));
    }

    private static Result<PagedResult<CustomerDto>> Fail(Error error) => Result.Failure<PagedResult<CustomerDto>>(error);
}
