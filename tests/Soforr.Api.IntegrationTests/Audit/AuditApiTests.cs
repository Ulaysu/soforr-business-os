using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Audit;
using Soforr.Domain.Identity;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Audit;

/// <summary>
/// The audit trail through the running application: what registering, signing in, being locked out and signing out
/// record, that an event belongs to the request that caused it, who may read the trail, and that it is tenant-scoped.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public AuditApiTests(PostgresDatabaseFixture database)
    {
        _database = database;
        _factory = CreateFactory(database.ConnectionString);
    }

    public void Dispose()
    {
        _clients.ForEach(client => client.Dispose());
        _factory.Dispose();
    }

    private HttpClient NewAnonymousClient()
    {
        var client = NewClient(_factory);
        _clients.Add(client);
        return client;
    }

    private static async Task<JsonElement[]> TrailAsync(HttpClient owner, string query = "")
    {
        var response = await owner.GetAsync(Relative("/api/v1/audit-events" + query));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    // ---- registration

    [Fact]
    public async Task Registering_RecordsTheRegistrationAndTheOwnerRole_InTheNewOrganization()
    {
        var client = NewAnonymousClient();
        var email = NewEmail();
        var registered = await ReadAsync(await RegisterAsync(client, email));
        var userId = registered.GetProperty("userId").GetGuid();
        var organizationId = registered.GetProperty("organizationId").GetGuid();

        await using var db = _database.CreateContext();
        var events = await db.Set<AuditEvent>().IgnoreQueryFilters().Where(e => e.OrganizationId == organizationId).OrderBy(e => e.Action).ToListAsync();

        Assert.Equal(["membership.role_assigned", "user.registered"], events.Select(e => e.Action).ToArray());
        Assert.All(events, e => Assert.Equal(userId, e.ActorId));
        var role = events.Single(e => e.Action == "membership.role_assigned");
        Assert.Equal("Membership", role.EntityType);
        Assert.Contains("Owner", role.Description, StringComparison.Ordinal);
        var registration = events.Single(e => e.Action == "user.registered");
        Assert.Equal("User", registration.EntityType);
        Assert.Equal(userId, registration.EntityId);
    }

    [Fact]
    public async Task ARegistrationThatFails_RecordsNothing()
    {
        var client = NewAnonymousClient();
        var email = NewEmail();
        await RegisterAsync(client, email);
        await using var before = _database.CreateContext();
        var eventsBefore = await before.Set<AuditEvent>().IgnoreQueryFilters().CountAsync();

        var duplicate = await RegisterAsync(client, email.ToUpperInvariant());
        var invalid = await RegisterAsync(client, NewEmail(), organizationName: "");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await using var after = _database.CreateContext();
        Assert.Equal(eventsBefore, await after.Set<AuditEvent>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task AnEvent_CarriesTheCorrelationIdOfTheRequestThatCausedIt()
    {
        var client = NewAnonymousClient();
        using var tokenResponse = await client.GetAsync(Relative("/api/v1/auth/antiforgery"));
        var token = await ReadAsync(tokenResponse);
        using var request = new HttpRequestMessage(HttpMethod.Post, Relative("/api/v1/auth/register"))
        {
            Content = JsonContent.Create(new { email = NewEmail(), password = Password, organizationName = "Corr Ltd", currency = "GMD", timeZoneId = "UTC" }),
        };
        request.Headers.Add(token.GetProperty("headerName").GetString()!, token.GetProperty("token").GetString());
        request.Headers.Add("X-Correlation-ID", "audit-corr-12345");

        var response = await client.SendAsync(request);
        var organizationId = (await ReadAsync(response)).GetProperty("organizationId").GetGuid();

        Assert.Equal("audit-corr-12345", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        await using var db = _database.CreateContext();
        var events = await db.Set<AuditEvent>().IgnoreQueryFilters().Where(e => e.OrganizationId == organizationId).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("audit-corr-12345", e.CorrelationId));
    }

    // ---- sign-in, lockout, sign-out

    [Fact]
    public async Task SigningIn_AndSigningOut_AreRecordedForTheUser()
    {
        var client = NewAnonymousClient();
        var (userId, organizationId, _) = await RegisterAndSignInAsync(client);

        var signedOut = await PostAsync(client, "/api/v1/auth/logout", body: null);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        await using var db = _database.CreateContext();
        var events = await db.Set<AuditEvent>().IgnoreQueryFilters().Where(e => e.OrganizationId == organizationId && e.Action.StartsWith("auth.")).OrderBy(e => e.OccurredAtUtc).ToListAsync();
        Assert.Equal(["auth.sign_in_succeeded", "auth.signed_out"], events.Select(e => e.Action).ToArray());
        Assert.All(events, e =>
        {
            Assert.Equal(userId, e.ActorId);
            Assert.Equal("User", e.EntityType);
            Assert.Equal(userId, e.EntityId);
        });
    }

    [Fact]
    public async Task ASignOutWithoutASession_RecordsNothing()
    {
        var client = NewAnonymousClient();
        await using var before = _database.CreateContext();
        var eventsBefore = await before.Set<AuditEvent>().IgnoreQueryFilters().CountAsync();

        var response = await PostAsync(client, "/api/v1/auth/logout", body: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var after = _database.CreateContext();
        Assert.Equal(eventsBefore, await after.Set<AuditEvent>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task AWrongPasswordIsNotAudited_ButTheLockoutIsRecordedOnce_EvenWhenTheAttemptsContinue()
    {
        var client = NewAnonymousClient();
        var email = NewEmail();
        var registered = await ReadAsync(await RegisterAsync(client, email));
        var organizationId = registered.GetProperty("organizationId").GetGuid();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(client, email, "wrong password!!");
        }

        // Further attempts, with the wrong and with the right password, are refused while locked out.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(client, email, "still wrong!!!!");
        }

        await LoginAsync(client, email);

        await using var db = _database.CreateContext();
        var locked = await db.Set<AuditEvent>().IgnoreQueryFilters().Where(e => e.OrganizationId == organizationId && e.Action == "auth.account_locked").ToListAsync();
        var signIns = await db.Set<AuditEvent>().IgnoreQueryFilters().CountAsync(e => e.OrganizationId == organizationId && e.Action == "auth.sign_in_succeeded");
        var lockEvent = Assert.Single(locked);
        Assert.Equal(registered.GetProperty("userId").GetGuid(), lockEvent.ActorId);
        Assert.Equal(0, signIns);
    }

    [Fact]
    public async Task AFailedSignInForAnUnknownAddress_RecordsNothing()
    {
        var client = NewAnonymousClient();
        await using var before = _database.CreateContext();
        var eventsBefore = await before.Set<AuditEvent>().IgnoreQueryFilters().CountAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await LoginAsync(client, NewEmail());
        }

        await using var after = _database.CreateContext();
        Assert.Equal(eventsBefore, await after.Set<AuditEvent>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task NoEvent_ContainsAnEmailAddressOrAPassword()
    {
        var client = NewAnonymousClient();
        var email = NewEmail();
        var registered = await ReadAsync(await RegisterAsync(client, email));
        var organizationId = registered.GetProperty("organizationId").GetGuid();
        await LoginAsync(client, email);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(client, email, "wrong password!!");
        }

        await using var db = _database.CreateContext();
        var events = await db.Set<AuditEvent>().IgnoreQueryFilters().Where(e => e.OrganizationId == organizationId).ToListAsync();

        Assert.NotEmpty(events);
        Assert.All(events, e =>
        {
            var text = string.Join(' ', e.Action, e.EntityType, e.Description, e.CorrelationId);
            Assert.DoesNotContain(email, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong password!!", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---- reading the trail

    [Fact]
    public async Task TheOwner_SeesTheirOrganizationsTrail_NewestFirst()
    {
        var owner = NewAnonymousClient();
        await RegisterAndSignInAsync(owner);

        var items = await TrailAsync(owner);

        Assert.Equal(
            ["auth.sign_in_succeeded", "membership.role_assigned", "user.registered"],
            items.Select(i => i.GetProperty("action").GetString()!).Order(StringComparer.Ordinal).ToArray());
        var times = items.Select(i => i.GetProperty("occurredAtUtc").GetDateTimeOffset()).ToArray();
        Assert.Equal(times.OrderByDescending(time => time).ToArray(), times);
        Assert.Equal("auth.sign_in_succeeded", items[0].GetProperty("action").GetString());
        var names = items[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["action", "actorId", "correlationId", "description", "entityId", "entityType", "id", "occurredAtUtc"], names);
    }

    [Fact]
    public async Task TheTrail_CanBeFilteredAndPaged_OverHttp()
    {
        var owner = NewAnonymousClient();
        var (userId, _, _) = await RegisterAndSignInAsync(owner);

        var byAction = await TrailAsync(owner, "?action=membership.role_assigned");
        var byActor = await TrailAsync(owner, $"?actorId={userId}&pageSize=2");
        var badSize = await owner.GetAsync(Relative("/api/v1/audit-events?pageSize=500"));
        var badDate = await owner.GetAsync(Relative("/api/v1/audit-events?from=not-a-date"));

        Assert.Single(byAction);
        Assert.Equal(2, byActor.Length);
        Assert.Equal(HttpStatusCode.BadRequest, badSize.StatusCode);
        Assert.Equal("pagination.page_size_invalid", (await ReadAsync(badSize)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);
    }

    [Theory]
    [InlineData("Anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("Owner", HttpStatusCode.OK)]
    [InlineData("Manager", HttpStatusCode.Forbidden)]
    [InlineData("Staff", HttpStatusCode.Forbidden)]
    public async Task OnlyTheOwner_CanReadTheTrail(string principal, HttpStatusCode expected)
    {
        var owner = NewAnonymousClient();
        var (_, organization, _) = await RegisterAndSignInAsync(owner);
        HttpClient client;
        if (principal == "Anonymous")
        {
            client = NewAnonymousClient();
        }
        else if (principal == "Owner")
        {
            client = owner;
        }
        else
        {
            client = await SignInAsMemberAsync(_factory, _database, organization, Enum.Parse<Role>(principal));
            _clients.Add(client);
        }

        var response = await client.GetAsync(Relative("/api/v1/audit-events"));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ATenantOwner_SeesOnlyTheirOwnEvents()
    {
        var first = NewAnonymousClient();
        var second = NewAnonymousClient();
        var (firstUser, _, _) = await RegisterAndSignInAsync(first);
        var (secondUser, _, _) = await RegisterAndSignInAsync(second);

        var mine = await TrailAsync(first, "?pageSize=100");
        var theirs = await TrailAsync(second, "?pageSize=100");

        Assert.All(mine, item => Assert.Equal(firstUser, item.GetProperty("actorId").GetGuid()));
        Assert.All(theirs, item => Assert.Equal(secondUser, item.GetProperty("actorId").GetGuid()));
        Assert.DoesNotContain(theirs, item => item.GetProperty("actorId").GetGuid() == firstUser);
    }

    [Fact]
    public async Task ThereIsNoEndpoint_ToCreateChangeOrDeleteAnEvent()
    {
        var owner = NewAnonymousClient();
        await RegisterAndSignInAsync(owner);
        var first = (await TrailAsync(owner))[0].GetProperty("id").GetGuid();

        var post = await SendAsync(owner, HttpMethod.Post, "/api/v1/audit-events", new { action = "x.y" });
        var put = await SendAsync(owner, HttpMethod.Put, $"/api/v1/audit-events/{first}", new { action = "x.y" });
        var delete = await SendAsync(owner, HttpMethod.Delete, $"/api/v1/audit-events/{first}", body: null);

        Assert.All([post, put, delete], response => Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed));
        Assert.Contains(await TrailAsync(owner), item => item.GetProperty("id").GetGuid() == first);
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheTrail_AndNoEntitySchema()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/audit-events").GetProperty("get");
        Assert.Equal("ListAuditEvents", operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("AuditEventDto", schemas);
        Assert.DoesNotContain("AuditEvent", schemas);
    }
}
