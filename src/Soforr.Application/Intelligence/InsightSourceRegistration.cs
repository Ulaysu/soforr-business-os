using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Soforr.Application.Intelligence;

public static class InsightSourceRegistration
{
    /// <summary>Registers every <see cref="IInsightSource"/> found in <paramref name="assembly"/>, so a new capability appears without listing it anywhere.</summary>
    public static IServiceCollection AddInsightSources(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return services.AddInsightSources(assembly.GetTypes());
    }

    /// <summary>Registers each concrete type that implements <see cref="IInsightSource"/>, with a scoped lifetime (sources read through the request's database context).</summary>
    public static IServiceCollection AddInsightSources(this IServiceCollection services, IEnumerable<Type> candidateTypes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(candidateTypes);

        foreach (var type in candidateTypes.Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(IInsightSource).IsAssignableFrom(t)))
        {
            services.AddScoped(typeof(IInsightSource), type);
        }

        return services;
    }
}
