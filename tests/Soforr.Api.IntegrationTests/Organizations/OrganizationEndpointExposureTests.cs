using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Soforr.Api.IntegrationTests.Organizations;

/// <summary>
/// The Organization endpoints require a signed-in user with the right permission (IDENTITY-002), so they exist in
/// every environment and answer 401 to anonymous callers. These tests need no database: they only check routing
/// and the default-deny policy, which runs before any handler.
/// </summary>
public class OrganizationEndpointExposureTests
{
    private static readonly Guid SomeId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a10");
    private static readonly Uri Item = new($"/api/v1/organizations/{SomeId}", UriKind.Relative);
    private static readonly Uri Settings = new($"/api/v1/organizations/{SomeId}/settings", UriKind.Relative);

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task InEveryEnvironment_AnAnonymousCaller_Gets401_NotData(string environment)
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = factory.CreateClient();

        var get = await client.GetAsync(Item);
        var update = await client.PutAsJsonAsync(Settings, new { name = "Name", timeZoneId = "UTC" });

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ARouteThatDoesNotExist_Stays404_ForAnAnonymousCaller_NotA401(string environment)
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = factory.CreateClient();

        var unknown = await client.GetAsync(new Uri("/api/v1/nothing-here", UriKind.Relative));
        var withExtension = await client.GetAsync(new Uri("/some/file.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withExtension.StatusCode);
    }

    [Fact]
    public async Task InDevelopment_TheOpenApiDocument_DocumentsTheOrganizationEndpoints_AndNoCreate()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        Assert.False(paths.TryGetProperty("/api/v1/organizations", out _));

        var get = paths.GetProperty("/api/v1/organizations/{id}").GetProperty("get");
        Assert.Equal("GetOrganization", get.GetProperty("operationId").GetString());
        Assert.True(get.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(get.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(get.GetProperty("responses").TryGetProperty("404", out _));

        var update = paths.GetProperty("/api/v1/organizations/{id}/settings").GetProperty("put");
        Assert.Equal("UpdateOrganizationSettings", update.GetProperty("operationId").GetString());
        Assert.True(update.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(update.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(update.GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(update.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(update.TryGetProperty("requestBody", out _));
    }

    [Fact]
    public async Task TheOpenApiDocument_DoesNotExposeTheDomainEntity()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        using var document = JsonDocument.Parse(json);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("OrganizationDto", schemas);
        Assert.DoesNotContain("Organization", schemas);
        Assert.DoesNotContain("Currency", schemas);
    }
}
