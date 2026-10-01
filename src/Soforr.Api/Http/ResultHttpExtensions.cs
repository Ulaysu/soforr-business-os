using Soforr.Application.Common;

namespace Soforr.Api.Http;

/// <summary>
/// The one place where a handler <see cref="Result{T}"/> becomes an HTTP response (ADR-0003). A failure becomes an
/// RFC 9457 problem response carrying the stable error <c>code</c>; the problem pipeline adds the
/// <c>correlationId</c>. Endpoints contain no mapping of their own.
/// </summary>
public static class ResultHttpExtensions
{
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result.IsSuccess ? onSuccess(result.Value) : ToProblem(result.Error!);
    }

    public static IResult ToProblem(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        return error.Kind switch
        {
            // Validation problems list the failure per input field (an empty key means "not about one field").
            ErrorKind.Validation => Results.ValidationProblem(
                new Dictionary<string, string[]> { [error.Field ?? string.Empty] = [error.Message] },
                title: "One or more validation errors occurred.",
                extensions: extensions),
            ErrorKind.NotFound => Results.Problem(error.Message, statusCode: StatusCodes.Status404NotFound, title: "Not Found", extensions: extensions),
            ErrorKind.Forbidden => Results.Problem(error.Message, statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", extensions: extensions),
            ErrorKind.Conflict => Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict", extensions: extensions),
            ErrorKind.RuleViolation => Results.Problem(error.Message, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Unprocessable Entity", extensions: extensions),
            _ => throw new ArgumentOutOfRangeException(nameof(error), error.Kind, "Unknown error kind."),
        };
    }
}
