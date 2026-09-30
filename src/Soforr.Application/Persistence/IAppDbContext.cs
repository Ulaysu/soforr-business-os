using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace Soforr.Application.Persistence;

/// <summary>
/// The persistence port used by command and query handlers (ADR-0001). Implemented by Infrastructure.
/// Entity sets are exposed through <see cref="Set{TEntity}"/> as modules introduce their entities.
/// </summary>
public interface IAppDbContext
{
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Mirrors DbContext.Set<TEntity>(); the solution is C# only.")]
    DbSet<TEntity> Set<TEntity>()
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
