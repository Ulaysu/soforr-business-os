using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Soforr.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Infrastructure implementations of Application ports (persistence, external services).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services;
    }
}
