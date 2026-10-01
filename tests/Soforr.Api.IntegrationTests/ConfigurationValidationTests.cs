using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Soforr.Infrastructure.Configuration;

namespace Soforr.Api.IntegrationTests;

/// <summary>
/// Startup validation is tested with a plain generic host rather than WebApplicationFactory: when the full
/// application host fails to start, WebApplicationFactory's deferred host intermittently reports an
/// ObjectDisposedException instead of the startup exception, which made these assertions flaky.
/// </summary>
public class ConfigurationValidationTests
{
    private const string SectionName = "Sample";

    [Fact]
    public async Task Host_Starts_WhenRequiredSettingIsProvided()
    {
        using var host = BuildHost(new Dictionary<string, string?> { ["Sample:Name"] = "value" });

        await host.StartAsync();

        Assert.Equal("value", host.Services.GetRequiredService<IOptions<SampleOptions>>().Value.Name);
    }

    [Fact]
    public async Task Host_FailsAtStartup_NamingTheMissingSetting_WhenRequiredSettingIsMissing()
    {
        using var host = BuildHost(new Dictionary<string, string?> { ["Sample:Other"] = "x" });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(nameof(SampleOptions.Name), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_FailsAtStartup_NamingTheSection_WhenSectionIsMissing()
    {
        using var host = BuildHost(new Dictionary<string, string?>());

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(SectionName, exception.Message, StringComparison.Ordinal);
    }

    private static IHost BuildHost(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddValidatedOptions<SampleOptions>(builder.Configuration, SectionName);
        return builder.Build();
    }

    private sealed class SampleOptions
    {
        [Required]
        public string? Name { get; set; }

        public string? Other { get; set; }
    }
}
