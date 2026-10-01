using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        services.AddIdentityAuthentication(configuration);

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: [HealthCheckTags.Ready]);

        return services;
    }
}
