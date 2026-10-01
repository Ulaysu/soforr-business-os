using Soforr.Domain.Common;

namespace Soforr.Application.Common;

/// <summary>
/// The one place where a <see cref="DomainException"/> becomes an <see cref="Error"/> (ADR-0003). The Domain
/// enforces its invariants by throwing; handlers call the Domain through <see cref="Run{T}"/> so that a rejected
/// input is returned as a failed <see cref="Result{T}"/> instead of escaping as an exception.
/// </summary>
public static class DomainErrors
{
    /// <summary>
    /// Runs a Domain operation. A <see cref="DomainException"/> becomes a <see cref="ErrorKind.Validation"/> error
    /// carrying the exception's stable code and message, because these exceptions are raised by invariants on
    /// supplied input. <paramref name="fieldFor"/> maps an error code to the input field it is about.
    /// </summary>
    public static Result<T> Run<T>(Func<T> operation, Func<string, string?>? fieldFor = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return Result.Success(operation());
        }
        catch (DomainException exception)
        {
            return Result.Failure<T>(new Error(exception.Code, ErrorKind.Validation, exception.Message, fieldFor?.Invoke(exception.Code)));
        }
    }
}
