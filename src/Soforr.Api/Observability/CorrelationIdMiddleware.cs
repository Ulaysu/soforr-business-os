using Microsoft.Extensions.Primitives;
using Serilog.Context;

namespace Soforr.Api.Observability;

/// <summary>
/// Assigns every request a correlation ID, returns it in the response header, and pushes it into the log context.
/// A caller-supplied ID is honored only if it is short and made of safe characters, so untrusted input
/// cannot inject content into logs or responses.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string LogPropertyName = "CorrelationId";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = Resolve(context.Request.Headers[HeaderName]);
        context.SetCorrelationId(correlationId);

        // OnStarting runs after any exception handler has cleared the response, so the header is always present.
        context.Response.OnStarting(
            static state =>
            {
                var (httpContext, id) = ((HttpContext, string))state;
                httpContext.Response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            },
            (context, correlationId));

        using (LogContext.PushProperty(LogPropertyName, correlationId))
        {
            await next(context);
        }
    }

    private static string Resolve(StringValues values) =>
        values.Count == 1 && IsValid(values[0]) ? values[0]! : Guid.NewGuid().ToString("N");

    private static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
