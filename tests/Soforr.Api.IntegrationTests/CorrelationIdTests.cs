using System.Net;
using Serilog.Events;

namespace Soforr.Api.IntegrationTests;

public class CorrelationIdTests
{
    private const string HeaderName = "X-Correlation-ID";

    [Fact]
    public async Task Response_CarriesAGeneratedCorrelationId_WhenRequestHasNone()
    {
        await using var host = await PipelineTestHost.StartAsync("Production");

        var response = await host.Client.GetAsync(new Uri("/ok", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = Assert.Single(response.Headers.GetValues(HeaderName));
        Assert.False(string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public async Task EachRequestWithoutAnId_GetsADifferentCorrelationId()
    {
        await using var host = await PipelineTestHost.StartAsync("Production");

        var first = await host.Client.GetAsync(new Uri("/ok", UriKind.Relative));
        var second = await host.Client.GetAsync(new Uri("/ok", UriKind.Relative));

        Assert.NotEqual(first.Headers.GetValues(HeaderName).Single(), second.Headers.GetValues(HeaderName).Single());
    }

    [Fact]
    public async Task ValidCorrelationId_FromTheCaller_IsEchoed()
    {
        await using var host = await PipelineTestHost.StartAsync("Production");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/ok", UriKind.Relative));
        request.Headers.Add(HeaderName, "client-trace_01.A");

        var response = await host.Client.SendAsync(request);

        Assert.Equal("client-trace_01.A", Assert.Single(response.Headers.GetValues(HeaderName)));
    }

    [Theory]
    [InlineData("has spaces and !punctuation")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task UnsafeCorrelationId_FromTheCaller_IsReplaced(string supplied)
    {
        await using var host = await PipelineTestHost.StartAsync("Production");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/ok", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(HeaderName, supplied);

        var response = await host.Client.SendAsync(request);

        var returned = Assert.Single(response.Headers.GetValues(HeaderName));
        Assert.NotEqual(supplied, returned);
        Assert.Matches("^[A-Za-z0-9]{32}$", returned);
    }

    [Fact]
    public async Task RequestLogEvent_CarriesTheSameCorrelationId_AsTheResponseHeader()
    {
        await using var host = await PipelineTestHost.StartAsync("Production");

        var response = await host.Client.GetAsync(new Uri("/ok", UriKind.Relative));

        var headerId = Assert.Single(response.Headers.GetValues(HeaderName));
        var requestLog = Assert.Single(host.Logs.Events, e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Information, requestLog.Level);
        Assert.Equal(headerId, CapturingSink.CorrelationIdOf(requestLog));
    }
}
