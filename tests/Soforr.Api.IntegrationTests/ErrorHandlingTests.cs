using System.Net;
using System.Text.Json;
using Serilog.Events;

namespace Soforr.Api.IntegrationTests;

public class ErrorHandlingTests
{
    private const string HeaderName = "X-Correlation-ID";

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnhandledException_ReturnsGenericProblemDetails_WithoutInternals(string environment)
    {
        await using var host = await PipelineTestHost.StartAsync(environment);

        var response = await host.Client.GetAsync(new Uri("/boom", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(500, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            Assert.Single(response.Headers.GetValues(HeaderName)),
            document.RootElement.GetProperty("correlationId").GetString());

        Assert.DoesNotContain(PipelineTestHost.SecretExceptionMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("exception", out _));
        Assert.False(document.RootElement.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task UnhandledException_IsLoggedWithTheExceptionAndTheCorrelationId()
    {
        await using var host = await PipelineTestHost.StartAsync("Production");

        var response = await host.Client.GetAsync(new Uri("/boom", UriKind.Relative));

        var headerId = Assert.Single(response.Headers.GetValues(HeaderName));
        var errorLog = Assert.Single(host.Logs.Events, e => e.Level == LogEventLevel.Error && e.Exception is not null);
        Assert.IsType<InvalidOperationException>(errorLog.Exception);
        Assert.Equal(headerId, CapturingSink.CorrelationIdOf(errorLog));
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetails_WithCorrelationId()
    {
        await using var host = await PipelineTestHost.StartAsync("Production");

        var response = await host.Client.GetAsync(new Uri("/does-not-exist", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            Assert.Single(response.Headers.GetValues(HeaderName)),
            document.RootElement.GetProperty("correlationId").GetString());
    }
}
