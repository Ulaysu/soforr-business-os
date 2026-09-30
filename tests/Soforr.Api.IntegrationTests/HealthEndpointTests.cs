using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Soforr.Application.Persistence;

namespace Soforr.Api.IntegrationTests;

public class HealthEndpointTests
{
    [Fact]
    public async Task LivenessEndpoint_ReportsHealthy_WithoutTouchingTheDatabase_AndCarriesACorrelationId()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }

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
}
