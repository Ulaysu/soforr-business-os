using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Persistence;
using Soforr.Infrastructure.Common;
using Soforr.Infrastructure.Configuration;
using Soforr.Infrastructure.Identity;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Infrastructure implementations of Application ports (persistence, external services).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidV7IdGenerator>();

        services.AddValidatedOptions<ConnectionStringsOptions>(configuration, ConnectionStringsOptions.SectionName);

        services.AddDbContext<AppDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IOptions<ConnectionStringsOptions>>().Value.Database));
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());

        // The offline settings have defaults, so the section is optional; a value out of range stops the application at start.
        services.AddOptions<OfflineOptions>()
            .Bind(configuration.GetSection(OfflineOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.IsConsistent, "The Offline settings are inconsistent: MinGap must be at least twice ContactInterval, and GrantMaxAge at least Window.")
            .ValidateOnStart();
        services.AddSingleton<Soforr.Application.Offline.IOfflineSettings, OfflineSettings>();

        services.TryAddScoped<ICorrelationContext, NoCorrelationContext>();
        services.AddIdentityAuthentication(configuration);

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: [HealthCheckTags.Ready])
            .AddCheck<PendingMigrationsHealthCheck>("migrations", tags: [HealthCheckTags.Ready]);

        return services;
    }
}
