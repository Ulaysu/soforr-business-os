using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Soforr.Application.Common;
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
    public void ClockAndIdGeneratorPorts_AreProvidedByInfrastructure()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var ids = scope.ServiceProvider.GetRequiredService<IIdGenerator>();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
        Assert.InRange(clock.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var generated = Enumerable.Range(0, 100).Select(_ => ids.NewId()).ToArray();
        Assert.DoesNotContain(Guid.Empty, generated);
        Assert.Equal(generated.Length, generated.Distinct().Count());
        Assert.All(generated, id => Assert.Equal(7, id.Version));
    }

    [Fact]
    public void TheClock_ReturnsWholeMicroseconds_SoStoredTimestampsReadBackUnchanged()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        // Truncation is invisible in one sample 1 time in 10, so take many.
        var remainders = Enumerable.Range(0, 200).Select(_ => clock.UtcNow.Ticks % TimeSpan.TicksPerMicrosecond).ToArray();

        Assert.All(remainders, remainder => Assert.Equal(0, remainder));
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
