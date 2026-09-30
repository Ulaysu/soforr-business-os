using Microsoft.Extensions.DependencyInjection;

namespace Soforr.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Application-layer services. Command/query handlers are registered here as modules arrive.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        return services;
    }
}
