using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Identity;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Organizations;

/// <summary>
/// The authorization matrix of the Organization endpoints (ADR 0002): who (anonymous, each role, a member of
/// another organization) may do what, through the real application. Every protected operation appears here.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrganizationAuthorizationTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public OrganizationAuthorizationTests(PostgresDatabaseFixture database)
    {
        _database = database;
        _factory = CreateFactory(database.ConnectionString);
    }

    public void Dispose()
    {
        _clients.ForEach(client => client.Dispose());
        _factory.Dispose();
    }

    public enum Operation
    {
        GetOwnOrganization,
        UpdateOwnSettings,
        GetAnotherOrganization,
        UpdateAnotherOrganization,
    }

    private static async Task<HttpResponseMessage> PerformAsync(HttpClient client, Operation operation, Guid own, Guid other) => operation switch
    {
        Operation.GetOwnOrganization => await client.GetAsync(Relative($"/api/v1/organizations/{own}")),
        Operation.GetAnotherOrganization => await client.GetAsync(Relative($"/api/v1/organizations/{other}")),
        Operation.UpdateOwnSettings => await SendAsync(client, HttpMethod.Put, $"/api/v1/organizations/{own}/settings", new { name = "Changed", timeZoneId = "UTC" }),
        Operation.UpdateAnotherOrganization => await SendAsync(client, HttpMethod.Put, $"/api/v1/organizations/{other}/settings", new { name = "Hijacked", timeZoneId = "UTC" }),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    // The expected status for every principal and operation. Anonymous: always 401. A foreign organization is 404
    // for every role that may use the operation at all (it must not reveal that it exists), and 403 where the role
    // lacks the permission, whatever the id.
    public static TheoryData<string, Operation, HttpStatusCode> Matrix()
    {
        var data = new TheoryData<string, Operation, HttpStatusCode>();
        foreach (var operation in Enum.GetValues<Operation>())
        {
            data.Add("Anonymous", operation, HttpStatusCode.Unauthorized);
        }

        data.Add("Owner", Operation.GetOwnOrganization, HttpStatusCode.OK);
        data.Add("Owner", Operation.UpdateOwnSettings, HttpStatusCode.OK);
        data.Add("Owner", Operation.GetAnotherOrganization, HttpStatusCode.NotFound);
        data.Add("Owner", Operation.UpdateAnotherOrganization, HttpStatusCode.NotFound);

        foreach (var role in new[] { "Manager", "Staff" })
        {
            data.Add(role, Operation.GetOwnOrganization, HttpStatusCode.OK);
            data.Add(role, Operation.UpdateOwnSettings, HttpStatusCode.Forbidden);
            data.Add(role, Operation.GetAnotherOrganization, HttpStatusCode.NotFound);
            data.Add(role, Operation.UpdateAnotherOrganization, HttpStatusCode.Forbidden);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryOperation_IsAllowedOrDenied_AsTheMatrixSays(string principal, Operation operation, HttpStatusCode expected)
    {
        // Two organizations, each with an Owner. The principal under test belongs to the first.
        var ownerClient = NewClient(_factory);
        _clients.Add(ownerClient);
        var (_, own, _) = await RegisterAndSignInAsync(ownerClient);
        var otherClient = NewClient(_factory);
        _clients.Add(otherClient);
        var (_, other, _) = await RegisterAndSignInAsync(otherClient);

        HttpClient client;
        if (principal == "Anonymous")
        {
            client = NewClient(_factory);
            _clients.Add(client);
        }
        else if (principal == "Owner")
        {
            client = ownerClient;
        }
        else
        {
            client = await SignInAsMemberAsync(_factory, _database, own, Enum.Parse<Role>(principal));
            _clients.Add(client);
        }

        var response = await PerformAsync(client, operation, own, other);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ADeniedUpdate_ChangesNothing()
    {
        var ownerClient = NewClient(_factory);
        _clients.Add(ownerClient);
        var (_, own, _) = await RegisterAndSignInAsync(ownerClient);
        var staff = await SignInAsMemberAsync(_factory, _database, own, Role.Staff);
        _clients.Add(staff);

        var denied = await SendAsync(staff, HttpMethod.Put, $"/api/v1/organizations/{own}/settings", new { name = "Hijacked", timeZoneId = "UTC" });

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var body = await ReadAsync(await ownerClient.GetAsync(Relative($"/api/v1/organizations/{own}")));
        Assert.Equal("Soforr Trading", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AnUnauthenticatedAnswer_IsAProblemResponse_WithTheCorrelationId()
    {
        var anonymous = NewClient(_factory);
        _clients.Add(anonymous);

        var response = await anonymous.GetAsync(Relative($"/api/v1/organizations/{Guid.CreateVersion7()}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await ReadAsync(response)).TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task AForbiddenAnswer_CarriesTheStableCode()
    {
        var ownerClient = NewClient(_factory);
        _clients.Add(ownerClient);
        var (_, own, _) = await RegisterAndSignInAsync(ownerClient);
        var manager = await SignInAsMemberAsync(_factory, _database, own, Role.Manager);
        _clients.Add(manager);

        var response = await SendAsync(manager, HttpMethod.Put, $"/api/v1/organizations/{own}/settings", new { name = "X", timeZoneId = "UTC" });

        Assert.Equal("identity.forbidden", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnUnsafeOrganizationRequest_WithoutAnAntiforgeryToken_IsRejected_AndChangesNothing()
    {
        var ownerClient = NewClient(_factory);
        _clients.Add(ownerClient);
        var (_, own, _) = await RegisterAndSignInAsync(ownerClient);

        var response = await SendAsync(ownerClient, HttpMethod.Put, $"/api/v1/organizations/{own}/settings", new { name = "Forged", timeZoneId = "UTC" }, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery.invalid", (await ReadAsync(response)).GetProperty("code").GetString());
        var body = await ReadAsync(await ownerClient.GetAsync(Relative($"/api/v1/organizations/{own}")));
        Assert.Equal("Soforr Trading", body.GetProperty("name").GetString());
    }
}
