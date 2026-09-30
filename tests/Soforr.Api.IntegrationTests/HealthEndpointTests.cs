using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Soforr.Application.Persistence;

namespace Soforr.Api.IntegrationTests;

public class HealthEndpointTests
{
    [Fact]
    public async Task ReadinessEndpoint_ReportsUnhealthy_WhenDatabaseIsUnreachable()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void AppDbContext_IsResolvableThroughTheApplicationPort()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();

        var context = scope.ServiceProvider.GetService<IAppDbContext>();

        Assert.NotNull(context);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Host_FailsAtStartup_NamingTheSetting_WhenDatabaseConnectionStringIsMissing(string? connectionString)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = connectionString }));
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Database", exception.ToString(), StringComparison.Ordinal);
    }
}
