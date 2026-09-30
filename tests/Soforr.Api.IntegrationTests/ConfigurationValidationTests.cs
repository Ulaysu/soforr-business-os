using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Soforr.Infrastructure.Configuration;

namespace Soforr.Api.IntegrationTests;

public class ConfigurationValidationTests
{
    private const string SectionName = "Sample";

    [Fact]
    public void Host_Starts_WhenRequiredSettingIsProvided()
    {
        using var factory = CreateFactory(new Dictionary<string, string?> { ["Sample:Name"] = "value" });

        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<SampleOptions>>().Value;
        Assert.Equal("value", options.Name);
    }

    [Fact]
    public void Host_FailsAtStartup_NamingTheMissingSetting_WhenRequiredSettingIsMissing()
    {
        using var factory = CreateFactory(new Dictionary<string, string?> { ["Sample:Other"] = "x" });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var message = Flatten(exception);
        Assert.Contains(nameof(SampleOptions.Name), message, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_FailsAtStartup_NamingTheSection_WhenSectionIsMissing()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>());

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(SectionName, Flatten(exception), StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices((context, services) =>
                services.AddValidatedOptions<SampleOptions>(context.Configuration, SectionName));
        });

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            messages.Add(current.Message);
            if (current.InnerException is null)
            {
                break;
            }
        }

        return string.Join(" | ", messages);
    }

    private sealed class SampleOptions
    {
        [Required]
        public string? Name { get; set; }

        public string? Other { get; set; }
    }
}
