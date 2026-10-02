using Microsoft.Extensions.DependencyInjection;
using Soforr.Application.Common;

namespace Soforr.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Application-layer services: every command and query handler in this assembly.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<Inventory.StockMovementRecorder>();

        return services.AddHandlers(typeof(DependencyInjection).Assembly);
    }
}
