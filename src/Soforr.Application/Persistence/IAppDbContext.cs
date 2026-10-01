using Microsoft.EntityFrameworkCore;

namespace Soforr.Application.Persistence;

/// <summary>
/// The persistence port used by command and query handlers (ADR-0001). Implemented by Infrastructure.
/// Handlers may use LINQ and the async query extensions from the provider-agnostic
/// Microsoft.EntityFrameworkCore package over <see cref="Set{TEntity}"/>. Provider-specific or relational-only
/// operations (raw SQL, migrations, bulk updates) are not available here by design and belong behind
/// dedicated ports implemented in Infrastructure. Keep this surface provider-agnostic; it is guarded by
/// architecture tests.
/// </summary>
public interface IAppDbContext
{
    DbSet<TEntity> Set<TEntity>()
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
