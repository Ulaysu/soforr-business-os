using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Soforr.Api.IntegrationTests.Identity;

/// <summary>
/// Anyone could register through the authentication endpoints, which ADR 0002 allows only once e-mail verification
/// (IDENTITY-004) and rate limiting (SECURITY-002) exist, so they are mapped only in Development. These tests need
/// no database: they only check which routes exist.
/// </summary>
public class AuthEndpointExposureTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task OutsideDevelopment_NoAuthenticationEndpointExists(string environment)
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = factory.CreateClient();

        var antiforgery = await client.GetAsync(new Uri("/api/v1/auth/antiforgery", UriKind.Relative));
        var me = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));
        var register = await client.PostAsync(new Uri("/api/v1/auth/register", UriKind.Relative), content: null);
        var login = await client.PostAsync(new Uri("/api/v1/auth/login", UriKind.Relative), content: null);
        var logout = await client.PostAsync(new Uri("/api/v1/auth/logout", UriKind.Relative), content: null);

        Assert.All([antiforgery, me, register, login, logout], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    [Fact]
    public async Task InDevelopment_TheOpenApiDocument_DocumentsTheAuthenticationEndpoints_AndNoCredentialType()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("Register", paths.GetProperty("/api/v1/auth/register").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("SignIn", paths.GetProperty("/api/v1/auth/login").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("SignOut", paths.GetProperty("/api/v1/auth/logout").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("GetCurrentUser", paths.GetProperty("/api/v1/auth/me").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("GetAntiforgeryToken", paths.GetProperty("/api/v1/auth/antiforgery").GetProperty("get").GetProperty("operationId").GetString());

        // The credential record is never an API contract.
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("AccountDto", schemas);
        Assert.DoesNotContain("ApplicationUser", schemas);
        Assert.DoesNotContain("IdentityUser", json, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
    }
}
