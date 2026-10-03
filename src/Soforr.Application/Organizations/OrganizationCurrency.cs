using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Persistence;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;

namespace Soforr.Application.Organizations;

/// <summary>
/// The one place that answers "which currency is this organization's money in?". The Organization module owns the
/// currency (it is fixed at creation); every module that records money (Catalog prices, Purchasing orders, later Sales
/// and Expenses) obtains it here, in its handler, and passes it to its Domain. A request never carries a currency, and a
/// Domain never looks it up, so no caller can choose another currency and no module depends on another's persistence.
/// </summary>
public static class OrganizationCurrency
{
    /// <summary>The currency of the caller's organization, or <c>organization.not_found</c> when it cannot be resolved.</summary>
    public static async Task<Result<Currency>> ResolveAsync(IAppDbContext db, Guid? organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        // The organization query is filtered to the current organization, so another organization's id resolves to nothing.
        var organization = await db.Set<Organization>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == organizationId, cancellationToken);

        return organization is null
            ? Result.Failure<Currency>(OrganizationErrors.CurrentNotFound)
            : Result.Success(organization.Currency);
    }
}
