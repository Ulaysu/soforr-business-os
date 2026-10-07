using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Organizations;
using Soforr.Application.Persistence;
using Soforr.Domain.Audit;
using Soforr.Domain.Catalog;
using Soforr.Domain.Customers;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Domain.Purchasing;
using Soforr.Domain.Sales;
using Soforr.Infrastructure.Identity;

namespace Soforr.Infrastructure.Persistence;

/// <summary>
/// The application's database. It is also the Identity store (users, claims, logins, tokens; no Identity roles,
/// because a user's role is the Domain's membership, ADR 0002), so the credential record, the organization and the
/// membership can be saved in one transaction.
/// It also enforces tenant isolation (ADR 0002): every tenant-owned entity has a global query filter on the current
/// organization, and a save that would write another organization's row is refused. With no organization (an
/// anonymous request, a job without one) the filters match nothing, so the context fails closed.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IOrganizationContext organizationContext)
    : IdentityUserContext<ApplicationUser, Guid>(options), IAppDbContext
{
    // Read by the query filters; EF evaluates it per query for the context instance that runs the query.
    private Guid? CurrentOrganizationId => organizationContext.OrganizationId;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.OnModelCreating(builder);

        // Table names match the rest of the schema (no "AspNet" prefix).
        builder.Entity<ApplicationUser>().ToTable("Users");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("UserTokens");

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Tenant-owned entities. Each entity with an OrganizationId needs a filter here; a convention test fails if one
        // is missing. The Organization is the tenant itself, so its own id is the key. Its filter is defense in depth
        // (ADR 0002, decision B): the handlers also check ownership, and must keep doing so.
        builder.Entity<Organization>().HasQueryFilter(organization => CurrentOrganizationId != null && organization.Id == CurrentOrganizationId);
        builder.Entity<Membership>().HasQueryFilter(membership => CurrentOrganizationId != null && membership.OrganizationId == CurrentOrganizationId);
        builder.Entity<Product>().HasQueryFilter(product => CurrentOrganizationId != null && product.OrganizationId == CurrentOrganizationId);
        builder.Entity<StockLevel>().HasQueryFilter(level => CurrentOrganizationId != null && level.OrganizationId == CurrentOrganizationId);
        builder.Entity<InventoryMovement>().HasQueryFilter(movement => CurrentOrganizationId != null && movement.OrganizationId == CurrentOrganizationId);
        builder.Entity<AuditEvent>().HasQueryFilter(auditEvent => CurrentOrganizationId != null && auditEvent.OrganizationId == CurrentOrganizationId);
        builder.Entity<Supplier>().HasQueryFilter(supplier => CurrentOrganizationId != null && supplier.OrganizationId == CurrentOrganizationId);
        builder.Entity<PurchaseOrder>().HasQueryFilter(order => CurrentOrganizationId != null && order.OrganizationId == CurrentOrganizationId);
        builder.Entity<PurchaseOrderLine>().HasQueryFilter(line => CurrentOrganizationId != null && line.OrganizationId == CurrentOrganizationId);
        builder.Entity<PurchaseReceipt>().HasQueryFilter(receipt => CurrentOrganizationId != null && receipt.OrganizationId == CurrentOrganizationId);
        builder.Entity<PurchaseReceiptLine>().HasQueryFilter(line => CurrentOrganizationId != null && line.OrganizationId == CurrentOrganizationId);
        builder.Entity<Customer>().HasQueryFilter(customer => CurrentOrganizationId != null && customer.OrganizationId == CurrentOrganizationId);
        builder.Entity<Sale>().HasQueryFilter(sale => CurrentOrganizationId != null && sale.OrganizationId == CurrentOrganizationId);
        builder.Entity<SaleLine>().HasQueryFilter(line => CurrentOrganizationId != null && line.OrganizationId == CurrentOrganizationId);
    }

    public void ClearChangeTracker() => ChangeTracker.Clear();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureWritesStayInTheCurrentOrganization();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureWritesStayInTheCurrentOrganization();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// Write-side isolation: when the request acts for an organization, no tenant-owned row of another organization
    /// may be added, changed or deleted, whatever id the caller supplied. Without an organization nothing is
    /// restricted here, because that is the situation of registration, which creates the tenant; only handlers that
    /// run before sign-in may do that (architecture test).
    /// </summary>
    private void EnsureWritesStayInTheCurrentOrganization()
    {
        EnsureAppendOnlyRecordsAreNotChanged();

        if (CurrentOrganizationId is not { } current)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var property = entry.Metadata.FindProperty("OrganizationId") ?? (entry.Entity is Organization ? entry.Metadata.FindProperty("Id") : null);
            if (property is null || entry.Metadata.GetDeclaredQueryFilters().Count == 0)
            {
                continue;
            }

            if (entry.Property(property.Name).CurrentValue is not Guid owner || owner != current)
            {
                throw new TenantIsolationException($"A write to a {entry.Metadata.ClrType.Name} outside the current organization was refused.");
            }
        }
    }

    /// <summary>
    /// The movement ledger and the audit trail are append-only (ADR 0004; AUDIT-001): a recorded movement or audit
    /// event is never changed or deleted, whatever the organization or the caller; a correction is a new record.
    /// This applies even without a current organization. It guards the application's own writes; the audit trail also
    /// has a database trigger, while a raw SQL statement bypasses this guard for movements like any EF guard.
    /// </summary>
    private void EnsureAppendOnlyRecordsAreNotChanged()
    {
        var changed = ChangeTracker.Entries()
            .Where(entry => entry.Entity is InventoryMovement or AuditEvent)
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (changed is not null)
        {
            throw new AppendOnlyViolationException($"A {changed.Metadata.ClrType.Name} is append-only: it cannot be changed or deleted. Record a new one instead.");
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        // Money is numeric(19,4); quantities (numeric(18,4)) are configured explicitly when they are introduced.
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
    }
}
