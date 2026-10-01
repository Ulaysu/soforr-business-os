using System.Net;
using System.Text.Json;
using Soforr.Application.Common;

namespace Soforr.Api.IntegrationTests.Organizations;

/// <summary>The central mapping from a handler error to an RFC 9457 response, for every error kind.</summary>
public class ErrorMappingTests
{
    [Theory]
    [InlineData(ErrorKind.Validation, HttpStatusCode.BadRequest)]
    [InlineData(ErrorKind.Unauthenticated, HttpStatusCode.Unauthorized)]
    [InlineData(ErrorKind.NotFound, HttpStatusCode.NotFound)]
    [InlineData(ErrorKind.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(ErrorKind.Conflict, HttpStatusCode.Conflict)]
    [InlineData(ErrorKind.RuleViolation, HttpStatusCode.UnprocessableEntity)]
    public async Task EveryErrorKind_MapsToItsStatusCode_AsProblemJson_WithTheStableCodeAndTheCorrelationId(ErrorKind kind, HttpStatusCode expectedStatus)
    {
        await using var host = await PipelineTestHost.StartAsync("Production");

        var response = await host.Client.GetAsync(new Uri($"/problem/{kind}", UriKind.Relative));

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expectedStatus, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("test.code", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            Assert.Single(response.Headers.GetValues("X-Correlation-ID")),
            document.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task AValidationError_ListsTheProblemUnderItsField()
    {
        await using var host = await PipelineTestHost.StartAsync("Production");

        var response = await host.Client.GetAsync(new Uri("/problem/Validation?field=name", UriKind.Relative));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var messages = document.RootElement.GetProperty("errors").GetProperty("name").EnumerateArray().Select(m => m.GetString()!).ToArray();
        Assert.Equal(["A test message."], messages);
    }

    [Fact]
    public async Task AValidationError_WithoutAField_IsListedUnderAnEmptyKey()
    {
        await using var host = await PipelineTestHost.StartAsync("Production");

        var response = await host.Client.GetAsync(new Uri("/problem/Validation", UriKind.Relative));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(string.Empty, out var messages));
        Assert.Equal(1, messages.GetArrayLength());
    }

    [Theory]
    [InlineData(ErrorKind.NotFound)]
    [InlineData(ErrorKind.Conflict)]
    [InlineData(ErrorKind.RuleViolation)]
    [InlineData(ErrorKind.Forbidden)]
    public async Task OtherErrorKinds_CarryTheMessageAsTheDetail_AndNoFieldErrors(ErrorKind kind)
    {
        await using var host = await PipelineTestHost.StartAsync("Production");

        var response = await host.Client.GetAsync(new Uri($"/problem/{kind}", UriKind.Relative));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("A test message.", document.RootElement.GetProperty("detail").GetString());
        Assert.False(document.RootElement.TryGetProperty("errors", out _));
    }
}
