using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Organizations;

namespace Soforr.Api.IntegrationTests.Organizations;

/// <summary>The Organization endpoints in the running application, on a real database.</summary>
[Trait("Category", "Integration")]
public sealed class OrganizationApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public OrganizationApiTests(PostgresDatabaseFixture database)
    {
        _database = database;
        _factory = new ApiFactory(database.ConnectionString).WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private async Task<JsonElement> CreateAsync(string name = "Soforr Trading", string currency = "GMD", string timeZoneId = "Europe/London")
    {
        var response = await _client.PostAsJsonAsync(Relative("/api/v1/organizations"), new { name, currency, timeZoneId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ---- create and get

    [Fact]
    public async Task Create_Returns201WithLocation_AndTheOrganizationCanBeFetched()
    {
        var response = await _client.PostAsJsonAsync(Relative("/api/v1/organizations"), new { name = " Soforr Trading ", currency = "gmd", timeZoneId = "Europe/London" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("Soforr Trading", created.RootElement.GetProperty("name").GetString());
        Assert.Equal("GMD", created.RootElement.GetProperty("currency").GetString());
        Assert.Equal("Europe/London", created.RootElement.GetProperty("timeZoneId").GetString());
        Assert.Equal($"/api/v1/organizations/{id}", response.Headers.Location?.OriginalString);

        var fetched = await _client.GetAsync(Relative($"/api/v1/organizations/{id}"));
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        using var document = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync());
        Assert.Equal(id, document.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Soforr Trading", document.RootElement.GetProperty("name").GetString());

        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.Set<Organization>().CountAsync(o => o.Id == id));
    }

    [Fact]
    public async Task TheResponseBody_ExposesTheDtoShape_NotTheDomainEntity()
    {
        var created = await CreateAsync();

        var names = created.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(["createdAtUtc", "currency", "id", "name", "timeZoneId", "updatedAtUtc"], names);
    }

    // ---- validation problems

    [Theory]
    [InlineData(null, "GMD", "UTC", "organization.name_required", "name")]
    [InlineData("", "GMD", "UTC", "organization.name_required", "name")]
    [InlineData("Name", "ZZZ", "UTC", "currency.invalid", "currency")]
    [InlineData("Name", null, "UTC", "currency.invalid", "currency")]
    [InlineData("Name", "GMD", "Nowhere/Land", "organization.timezone_invalid", "timeZoneId")]
    public async Task Create_WithInvalidInput_Returns400ValidationProblem_WithTheFieldTheCodeAndTheCorrelationId(
        string? name,
        string? currency,
        string? timeZoneId,
        string expectedCode,
        string expectedField)
    {
        var response = await _client.PostAsJsonAsync(Relative("/api/v1/organizations"), new { name, currency, timeZoneId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("errors").GetProperty(expectedField).GetArrayLength());
        Assert.Equal(
            Assert.Single(response.Headers.GetValues("X-Correlation-ID")),
            document.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Create_WithInvalidInput_StoresNothing()
    {
        var before = await CountAsync();

        await _client.PostAsJsonAsync(Relative("/api/v1/organizations"), new { name = "", currency = "GMD", timeZoneId = "UTC" });

        Assert.Equal(before, await CountAsync());
    }

    [Fact]
    public async Task AMalformedBody_Returns400Problem_NotAServerError()
    {
        using var content = new StringContent("{ this is not json", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync(Relative("/api/v1/organizations"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- not found

    [Fact]
    public async Task GetAnUnknownOrganization_Returns404Problem_WithTheStableCode()
    {
        var response = await _client.GetAsync(Relative($"/api/v1/organizations/{Guid.CreateVersion7()}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("organization.not_found", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task AnIdThatIsNotAGuid_IsNotRoutedToTheEndpoint()
    {
        var response = await _client.GetAsync(Relative("/api/v1/organizations/not-a-guid"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- update

    [Fact]
    public async Task UpdateSettings_ChangesNameAndTimeZone_AndReturnsTheOrganization()
    {
        var created = await CreateAsync();
        var id = created.GetProperty("id").GetGuid();

        var response = await _client.PutAsJsonAsync(Relative($"/api/v1/organizations/{id}/settings"), new { name = "Soforr Group", timeZoneId = "UTC" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var updated = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Soforr Group", updated.RootElement.GetProperty("name").GetString());
        Assert.Equal("UTC", updated.RootElement.GetProperty("timeZoneId").GetString());
        Assert.Equal("GMD", updated.RootElement.GetProperty("currency").GetString());
        Assert.Equal(created.GetProperty("createdAtUtc").GetDateTimeOffset(), updated.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset());

        await using var db = _database.CreateContext();
        var stored = await db.Set<Organization>().SingleAsync(o => o.Id == id);
        Assert.Equal("Soforr Group", stored.Name);
    }

    [Fact]
    public async Task UpdateSettings_CannotChangeTheCurrency_EvenIfTheBodyContainsOne()
    {
        var created = await CreateAsync(currency: "GMD");
        var id = created.GetProperty("id").GetGuid();

        var response = await _client.PutAsJsonAsync(Relative($"/api/v1/organizations/{id}/settings"), new { name = "Same", timeZoneId = "UTC", currency = "USD" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var updated = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("GMD", updated.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task UpdateSettings_WithInvalidInput_Returns400_AndChangesNothing()
    {
        var created = await CreateAsync(name: "Keep Me");
        var id = created.GetProperty("id").GetGuid();

        var response = await _client.PutAsJsonAsync(Relative($"/api/v1/organizations/{id}/settings"), new { name = "New name", timeZoneId = "Nowhere/Land" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("organization.timezone_invalid", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("timeZoneId", out _));

        await using var db = _database.CreateContext();
        var stored = await db.Set<Organization>().SingleAsync(o => o.Id == id);
        Assert.Equal("Keep Me", stored.Name);
        Assert.Equal("Europe/London", stored.TimeZoneId);
    }

    [Fact]
    public async Task UpdateSettings_OfAnUnknownOrganization_Returns404Problem()
    {
        var response = await _client.PutAsJsonAsync(Relative($"/api/v1/organizations/{Guid.CreateVersion7()}/settings"), new { name = "Name", timeZoneId = "UTC" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("organization.not_found", document.RootElement.GetProperty("code").GetString());
    }

    private async Task<int> CountAsync()
    {
        await using var db = _database.CreateContext();
        return await db.Set<Organization>().CountAsync();
    }
}
