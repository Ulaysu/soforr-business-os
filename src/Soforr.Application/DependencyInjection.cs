using Microsoft.Extensions.DependencyInjection;
using Soforr.Application.Common;
using Soforr.Application.Intelligence;

namespace Soforr.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Application-layer services: every command and query handler and every insight source in this assembly.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<Inventory.StockMovementRecorder>();
        services.AddScoped<Audit.IAuditLog, Audit.AuditLog>();
        services.AddScoped<Offline.OfflineEventTimeValidator>();

        services.AddInsightSources(typeof(DependencyInjection).Assembly);

        return services.AddHandlers(typeof(DependencyInjection).Assembly);
    }
}
