using System.Net;
using System.Text;
using System.Text.Json;
using Serilog.Events;

namespace Soforr.Api.IntegrationTests;

/// <summary>
/// A request the framework cannot read (a malformed body) is the caller's mistake and must be a 400 problem in every
/// environment. In Development minimal APIs throw instead of answering, which used to surface as a 500.
/// </summary>
public class ClientErrorTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AMalformedJsonBody_IsA400Problem_WithTheCorrelationId_AndNoInternals(string environment)
    {
        await using var host = await PipelineTestHost.StartAsync(environment);
        using var content = new StringContent("{ this is not json", Encoding.UTF8, "application/json");

        var response = await host.Client.PostAsync(new Uri("/echo", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            Assert.Single(response.Headers.GetValues("X-Correlation-ID")),
            document.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("Failed to read parameter", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EchoBody", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedBody_IsNotLoggedAsAnUnhandledServerError()
    {
        await using var host = await PipelineTestHost.StartAsync("Development");
        using var content = new StringContent("{ this is not json", Encoding.UTF8, "application/json");

        await host.Client.PostAsync(new Uri("/echo", UriKind.Relative), content);

        Assert.DoesNotContain(host.Logs.Events, e => e.Level >= LogEventLevel.Error);
    }

    [Fact]
    public async Task AValidBody_StillWorks()
    {
        await using var host = await PipelineTestHost.StartAsync("Development");
        using var content = new StringContent("{\"value\":\"hello\"}", Encoding.UTF8, "application/json");

        var response = await host.Client.PostAsync(new Uri("/echo", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }
}
