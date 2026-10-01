namespace Soforr.Api.Observability;

public static class HttpContextCorrelationExtensions
{
    private const string ItemKey = "Soforr.CorrelationId";

    /// <summary>The correlation ID of the current request, or null if the correlation middleware has not run.</summary>
    public static string? GetCorrelationId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(ItemKey, out var value) ? value as string : null;
    }

    internal static void SetCorrelationId(this HttpContext context, string correlationId) =>
        context.Items[ItemKey] = correlationId;
}
