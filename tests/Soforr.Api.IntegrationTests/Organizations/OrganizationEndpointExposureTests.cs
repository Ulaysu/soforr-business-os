using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Soforr.Api.IntegrationTests.Organizations;

/// <summary>
/// The Organization endpoints have no access control until IDENTITY-002, so they exist only in Development.
/// These tests need no database: they only check which routes exist.
/// </summary>
public class OrganizationEndpointExposureTests
{
    private static readonly Uri Collection = new("/api/v1/organizations", UriKind.Relative);
    private static readonly Uri Item = new($"/api/v1/organizations/{Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a10")}", UriKind.Relative);
    private static readonly Uri Settings = new($"/api/v1/organizations/{Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a10")}/settings", UriKind.Relative);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task OutsideDevelopment_NoOrganizationEndpointExists(string environment)
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync(Collection, new { name = "Name", currency = "GMD", timeZoneId = "UTC" });
        var get = await client.GetAsync(Item);
        var update = await client.PutAsJsonAsync(Settings, new { name = "Name", timeZoneId = "UTC" });

        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    [Fact]
    public async Task InDevelopment_TheOpenApiDocument_DocumentsTheOrganizationEndpoints()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        var create = paths.GetProperty("/api/v1/organizations").GetProperty("post");
        Assert.Equal("CreateOrganization", create.GetProperty("operationId").GetString());
        Assert.True(create.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(create.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(create.TryGetProperty("requestBody", out _));

        var get = paths.GetProperty("/api/v1/organizations/{id}").GetProperty("get");
        Assert.Equal("GetOrganization", get.GetProperty("operationId").GetString());
        Assert.True(get.GetProperty("responses").TryGetProperty("404", out _));

        var update = paths.GetProperty("/api/v1/organizations/{id}/settings").GetProperty("put");
        Assert.Equal("UpdateOrganizationSettings", update.GetProperty("operationId").GetString());
        Assert.True(update.GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(update.GetProperty("responses").TryGetProperty("400", out _));
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
