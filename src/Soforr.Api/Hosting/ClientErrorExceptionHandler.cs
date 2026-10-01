using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;

namespace Soforr.Api.Hosting;

/// <summary>
/// Turns a request the framework could not read (for example a malformed JSON body) into the status code it
/// carries, normally 400, instead of a 500. In Development minimal APIs throw <see cref="BadHttpRequestException"/>
/// for such requests; in other environments they answer directly. Handling it here keeps the behavior the same in
/// every environment. The response is a generic problem: the exception message can name internal types and is
/// never returned.
/// </summary>
internal sealed class ClientErrorExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        httpContext.Response.StatusCode = badRequest.StatusCode;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = badRequest.StatusCode,
                Title = ReasonPhrases.GetReasonPhrase(badRequest.StatusCode),
                Detail = "The request could not be read.",
            },
        });
    }
}
