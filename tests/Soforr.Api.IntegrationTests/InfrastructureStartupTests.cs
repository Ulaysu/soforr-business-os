using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Soforr.Infrastructure;

namespace Soforr.Api.IntegrationTests;

/// <summary>
/// Uses a plain generic host for the same reason as <see cref="ConfigurationValidationTests"/>.
/// </summary>
public class InfrastructureStartupTests
{
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Host_FailsAtStartup_NamingTheSetting_WhenDatabaseConnectionStringIsMissing(string? connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = connectionString });
        builder.Services.AddInfrastructure(builder.Configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Database", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_Starts_WhenDatabaseConnectionStringIsProvided()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = ApiFactory.UnreachableConnectionString });
        builder.Services.AddInfrastructure(builder.Configuration);
        using var host = builder.Build();

        await host.StartAsync();
    }
}
