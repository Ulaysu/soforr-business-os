using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Soforr.Infrastructure.Configuration;

public static class OptionsExtensions
{
    /// <summary>
    /// Binds a required configuration section to <typeparamref name="TOptions"/>, validates it with
    /// data annotations, and validates it when the host starts so misconfiguration fails fast.
    /// A missing section throws immediately, naming the section.
    /// </summary>
    public static OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        return services.AddOptions<TOptions>()
            .Bind(configuration.GetRequiredSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
