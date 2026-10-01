namespace Soforr.Application.Common;

/// <summary>Handles one read-only query. Queries project to DTOs, never return domain entities, and never save.</summary>
public interface IQueryHandler<in TQuery, TResult>
{
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
