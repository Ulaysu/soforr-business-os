namespace Soforr.Application.Common;

/// <summary>
/// Handles one state-changing command. A command is one unit of work: the handler adds all changes to the
/// persistence context and saves once (ADR-0003). Handlers do not call other handlers.
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
