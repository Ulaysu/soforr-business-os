using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Soforr.Infrastructure.Configuration;

public static class OptionsExtensions
{
    /// <summary>
    /// Binds a required configuration section to <typeparamref name="TOptions"/>, validates it with
    /// data annotations, and validates it when the host starts so misconfiguration fails fast.
    /// A missing section fails at startup, naming the section. The section is read lazily, so configuration
    /// sources added after registration (for example by the host or by tests) are honored.
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

        var section = configuration.GetSection(sectionName);

        return services.AddOptions<TOptions>()
            .Bind(section)
            .Validate(_ => section.Exists(), $"Section '{sectionName}' not found in configuration.")
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
