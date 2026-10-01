using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Soforr.Application.Common;

public static class HandlerRegistration
{
    /// <summary>Registers every command and query handler found in <paramref name="assembly"/>.</summary>
    public static IServiceCollection AddHandlers(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return services.AddHandlers(assembly.GetTypes());
    }

    /// <summary>
    /// Registers each handler class as itself and as the handler interface(s) it implements, with a scoped lifetime.
    /// A command or query has exactly one handler: a second handler for the same interface is a configuration error.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two handlers implement the same handler interface.</exception>
    public static IServiceCollection AddHandlers(this IServiceCollection services, IEnumerable<Type> candidateTypes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(candidateTypes);

        foreach (var type in candidateTypes.Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }))
        {
            var handlerInterfaces = type.GetInterfaces().Where(IsHandlerInterface).ToArray();
            if (handlerInterfaces.Length == 0)
            {
                continue;
            }

            services.AddScoped(type);

            foreach (var handlerInterface in handlerInterfaces)
            {
                if (services.Any(descriptor => descriptor.ServiceType == handlerInterface))
                {
                    throw new InvalidOperationException($"More than one handler is registered for {handlerInterface}. Each command or query has exactly one handler.");
                }

                var concreteType = type;
                services.AddScoped(handlerInterface, provider => provider.GetRequiredService(concreteType));
            }
        }

        return services;
    }

    private static bool IsHandlerInterface(Type type) =>
        type.IsGenericType
        && type.GetGenericTypeDefinition() is var definition
        && (definition == typeof(ICommandHandler<,>) || definition == typeof(IQueryHandler<,>));
}
