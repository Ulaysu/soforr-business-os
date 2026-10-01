using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Identity;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Organizations;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Organizations;

/// <summary>The Organization endpoints in the running application, for a signed-in Owner, on a real database.</summary>
[Trait("Category", "Integration")]
public sealed class OrganizationApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public OrganizationApiTests(PostgresDatabaseFixture database)
    {
        _database = database;
        _factory = CreateFactory(database.ConnectionString);
        _client = NewClient(_factory);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string Own(Guid id) => $"/api/v1/organizations/{id}";

    private static string Settings(Guid id) => $"/api/v1/organizations/{id}/settings";

    private async Task<Guid> SignInAsOwnerAsync()
    {
        var (_, organizationId, _) = await RegisterAndSignInAsync(_client);
        return organizationId;
    }

    // ---- get

    [Fact]
    public async Task Get_ReturnsTheCallersOrganization_AsTheDto()
    {
        var id = await SignInAsOwnerAsync();

        var response = await _client.GetAsync(Relative(Own(id)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Soforr Trading", body.GetProperty("name").GetString());
        Assert.Equal("GMD", body.GetProperty("currency").GetString());
        var names = body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["createdAtUtc", "currency", "id", "name", "timeZoneId", "updatedAtUtc"], names);
    }

    [Fact]
    public async Task Get_OfAnUnknownOrganization_Returns404Problem_WithTheStableCode()
    {
        await SignInAsOwnerAsync();

        var response = await _client.GetAsync(Relative(Own(Guid.CreateVersion7())));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadAsync(response);
        Assert.Equal("organization.not_found", body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task AnIdThatIsNotAGuid_IsNotRoutedToTheEndpoint()
    {
        await SignInAsOwnerAsync();

        var response = await _client.GetAsync(Relative("/api/v1/organizations/not-a-guid"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- update

    [Fact]
    public async Task UpdateSettings_ChangesNameAndTimeZone_AndReturnsTheOrganization()
    {
        var id = await SignInAsOwnerAsync();

        var response = await SendAsync(_client, HttpMethod.Put, Settings(id), new { name = "Soforr Group", timeZoneId = "UTC" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync(response);
        Assert.Equal("Soforr Group", updated.GetProperty("name").GetString());
        Assert.Equal("UTC", updated.GetProperty("timeZoneId").GetString());
        Assert.Equal("GMD", updated.GetProperty("currency").GetString());

        await using var db = _database.CreateContext(id);
        Assert.Equal("Soforr Group", (await db.Set<Organization>().SingleAsync(o => o.Id == id)).Name);
    }

    [Fact]
    public async Task UpdateSettings_CannotChangeTheCurrency_EvenIfTheBodyContainsOne()
    {
        var id = await SignInAsOwnerAsync();

        var response = await SendAsync(_client, HttpMethod.Put, Settings(id), new { name = "Same", timeZoneId = "UTC", currency = "USD" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("GMD", (await ReadAsync(response)).GetProperty("currency").GetString());
    }

    [Fact]
    public async Task UpdateSettings_WithInvalidInput_Returns400WithTheFieldAndCode_AndChangesNothing()
    {
        var id = await SignInAsOwnerAsync();

        var response = await SendAsync(_client, HttpMethod.Put, Settings(id), new { name = "New name", timeZoneId = "Nowhere/Land" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync(response);
        Assert.Equal("organization.timezone_invalid", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("timeZoneId").GetArrayLength());
        Assert.Equal(
            Assert.Single(response.Headers.GetValues("X-Correlation-ID")),
            problem.GetProperty("correlationId").GetString());

        await using var db = _database.CreateContext(id);
        var stored = await db.Set<Organization>().SingleAsync(o => o.Id == id);
        Assert.Equal("Soforr Trading", stored.Name);
        Assert.Equal("Europe/London", stored.TimeZoneId);
    }

    [Fact]
    public async Task UpdateSettings_WithAMalformedBody_Returns400Problem_NotAServerError()
    {
        var id = await SignInAsOwnerAsync();
        using var tokenResponse = await _client.GetAsync(Relative("/api/v1/auth/antiforgery"));
        var token = await ReadAsync(tokenResponse);
        using var request = new HttpRequestMessage(HttpMethod.Put, Relative(Settings(id)))
        {
            Content = new StringContent("{ this is not json", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(token.GetProperty("headerName").GetString()!, token.GetProperty("token").GetString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UpdateSettings_OfAnUnknownOrganization_Returns404Problem()
    {
        await SignInAsOwnerAsync();

        var response = await SendAsync(_client, HttpMethod.Put, Settings(Guid.CreateVersion7()), new { name = "Name", timeZoneId = "UTC" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("organization.not_found", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ThereIsNoCreateEndpoint_OrganizationsAreCreatedByRegistering()
    {
        await SignInAsOwnerAsync();

        var response = await SendAsync(_client, HttpMethod.Post, "/api/v1/organizations", new { name = "Another", currency = "GMD", timeZoneId = "UTC" });

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }
}
