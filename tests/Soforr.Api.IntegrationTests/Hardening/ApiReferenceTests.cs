using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Soforr.Api.IntegrationTests.Hardening;

/// <summary>The API reference page (Scalar) exists only in Development, needs no database, and is the one place with a relaxed Content-Security-Policy.</summary>
public sealed class ApiReferenceTests
{
    [Fact]
    public async Task InDevelopment_TheReferencePageIsServed_WithoutSigningIn_AndPointsAtTheOpenApiDocument()
    {
        using var root = new ApiFactory();
        using var client = root.WithWebHostBuilder(builder => builder.UseEnvironment("Development")).CreateClient();

        var response = await client.GetAsync(new Uri("/scalar/v1", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("openapi/v1.json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheReferencePage_HasItsOwnPolicy_ThatAllowsNothingFromAnotherOrigin_AndEveryOtherResponseKeepsTheStrictOne()
    {
        using var root = new ApiFactory();
        using var client = root.WithWebHostBuilder(builder => builder.UseEnvironment("Development")).CreateClient();

        var page = await client.GetAsync(new Uri("/scalar/v1", UriKind.Relative));
        var other = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        var policy = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("connect-src 'self'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.DoesNotContain("http://", policy);
        Assert.DoesNotContain("https://", policy);
        Assert.DoesNotContain("*", policy);
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", other.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task OutsideDevelopment_TheReferencePageDoesNotExist()
    {
        using var root = new ApiFactory();
        using var client = root.WithWebHostBuilder(builder => builder.UseEnvironment("Production")).CreateClient();

        var response = await client.GetAsync(new Uri("/scalar/v1", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
