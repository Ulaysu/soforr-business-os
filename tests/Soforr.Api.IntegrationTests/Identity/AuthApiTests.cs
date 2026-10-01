using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Identity;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Identity;

/// <summary>
/// Registration, sign-in, sign-out, session lifetime and revocation in the running application, on a real database.
/// The client uses https because the session and antiforgery cookies are Secure and a client sends them only then.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly List<IDisposable> _disposables = [];

    public AuthApiTests(PostgresDatabaseFixture database)
    {
        _database = database;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private HttpClient CreateClient(Dictionary<string, string?>? settings = null)
    {
        var factory = AuthClient.CreateFactory(_database.ConnectionString, settings);
        var client = AuthClient.NewClient(factory);
        _disposables.Add(client);
        _disposables.Add(factory);
        return client;
    }

    private async Task<(HttpClient Client, string Email)> RegisteredAndSignedInAsync(Dictionary<string, string?>? settings = null)
    {
        var client = CreateClient(settings);
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client, email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email)).StatusCode);
        return (client, email);
    }

    // ---- register

    [Fact]
    public async Task Register_CreatesTheUser_TheOrganization_AndTheOwnerMembership_Together()
    {
        var client = CreateClient();
        var email = NewEmail();

        var response = await RegisterAsync(client, email, organizationName: " Soforr Trading ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        var userId = body.GetProperty("userId").GetGuid();
        var organizationId = body.GetProperty("organizationId").GetGuid();
        Assert.Equal("Owner", body.GetProperty("role").GetString());

        await using var db = _database.CreateContext();
        var user = await db.Set<ApplicationUser>().SingleAsync(candidate => candidate.Id == userId);
        Assert.Equal(email, user.Email);
        var organization = await db.Set<Organization>().IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == organizationId);
        Assert.Equal("Soforr Trading", organization.Name);
        var membership = await db.Set<Membership>().IgnoreQueryFilters().SingleAsync(candidate => candidate.UserId == userId);
        Assert.Equal(organizationId, membership.OrganizationId);
        Assert.Equal(Role.Owner, membership.Role);
    }

    [Fact]
    public async Task Register_StoresOnlyAHashOfThePassword_AndNeverReturnsIt()
    {
        var client = CreateClient();
        var email = NewEmail();

        var response = await RegisterAsync(client, email);

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);

        await using var db = _database.CreateContext();
        var user = await db.Set<ApplicationUser>().SingleAsync(candidate => candidate.Email == email);
        Assert.NotNull(user.PasswordHash);
        Assert.DoesNotContain(Password, user.PasswordHash, StringComparison.Ordinal);
        Assert.True(user.LockoutEnabled);
    }

    [Theory]
    [InlineData("", "correct horse battery staple", "Soforr", "GMD", "UTC", "email")]
    [InlineData("not-an-email", "correct horse battery staple", "Soforr", "GMD", "UTC", "email")]
    [InlineData("a@example.com", "", "Soforr", "GMD", "UTC", "password")]
    [InlineData("a@example.com", "short", "Soforr", "GMD", "UTC", "password")]
    [InlineData("a@example.com", "correct horse battery staple", " ", "GMD", "UTC", "organizationName")]
    [InlineData("a@example.com", "correct horse battery staple", "Soforr", "XXX", "UTC", "currency")]
    [InlineData("a@example.com", "correct horse battery staple", "Soforr", "GMD", "Nowhere/Land", "timeZoneId")]
    public async Task Register_WithInvalidInput_Is400WithTheFieldAndStoresNothing(string email, string password, string organizationName, string currency, string timeZoneId, string field)
    {
        var client = CreateClient();
        var uniqueEmail = email.Contains('@', StringComparison.Ordinal) && email.StartsWith("a@", StringComparison.Ordinal) ? NewEmail() : email;
        await using var before = _database.CreateContext();
        var usersBefore = await before.Set<ApplicationUser>().CountAsync();
        var organizationsBefore = await before.Set<Organization>().IgnoreQueryFilters().CountAsync();
        var membershipsBefore = await before.Set<Membership>().IgnoreQueryFilters().CountAsync();

        var response = await RegisterAsync(client, uniqueEmail, password, organizationName, currency, timeZoneId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), $"expected an error for '{field}'");

        await using var after = _database.CreateContext();
        Assert.Equal(usersBefore, await after.Set<ApplicationUser>().CountAsync());
        Assert.Equal(organizationsBefore, await after.Set<Organization>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(membershipsBefore, await after.Set<Membership>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Register_WithATooLongPassword_IsRejected()
    {
        var client = CreateClient();

        var response = await RegisterAsync(client, NewEmail(), new string('x', 129));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Register_WithAnAddressThatExistsInADifferentCase_Is409_AndCreatesNoSecondOrganization()
    {
        var client = CreateClient();
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client, email)).StatusCode);
        await using var before = _database.CreateContext();
        var organizationsBefore = await before.Set<Organization>().IgnoreQueryFilters().CountAsync();

        var response = await RegisterAsync(client, email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("identity.email_taken", (await ReadAsync(response)).GetProperty("code").GetString());
        await using var after = _database.CreateContext();
        Assert.Equal(organizationsBefore, await after.Set<Organization>().IgnoreQueryFilters().CountAsync());
    }

    // ---- sign in, current user, sign out

    [Fact]
    public async Task SignIn_StartsAHardenedCookieSession_AndMeReturnsTheOrganizationAndRole()
    {
        var client = CreateClient();
        var email = NewEmail();
        var registered = await ReadAsync(await RegisterAsync(client, email));

        var response = await LoginAsync(client, email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signedIn = await ReadAsync(response);
        Assert.Equal(registered.GetProperty("organizationId").GetGuid(), signedIn.GetProperty("organizationId").GetGuid());
        Assert.Equal("Owner", signedIn.GetProperty("role").GetString());
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-soforr.session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetAsync(Relative("/api/v1/auth/me"));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var account = await ReadAsync(me);
        Assert.Equal(registered.GetProperty("userId").GetGuid(), account.GetProperty("userId").GetGuid());
        Assert.Equal(registered.GetProperty("organizationId").GetGuid(), account.GetProperty("organizationId").GetGuid());
        Assert.Equal("Owner", account.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Me_WithoutASession_Is401()
    {
        var client = CreateClient();

        var response = await client.GetAsync(Relative("/api/v1/auth/me"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("identity.not_authenticated", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SignIn_WithAWrongPassword_AndWithAnUnknownAddress_GiveTheSameAnswer()
    {
        var client = CreateClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        var wrongPassword = await LoginAsync(client, email, "this is not the password");
        var unknownAddress = await LoginAsync(client, NewEmail());

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownAddress.StatusCode);
        var first = await ReadAsync(wrongPassword);
        var second = await ReadAsync(unknownAddress);
        Assert.Equal("identity.invalid_credentials", first.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("code").GetString(), second.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("detail").GetString(), second.GetProperty("detail").GetString());
        Assert.DoesNotContain(wrongPassword.Headers, header => header.Key == "Set-Cookie" && header.Value.Any(value => value.StartsWith("__Host-soforr.session=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SignIn_AfterTooManyFailures_LocksTheAccount_EvenForTheCorrectPassword()
    {
        var client = CreateClient();
        var email = NewEmail();
        await RegisterAsync(client, email);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, "wrong password!!")).StatusCode);
        }

        var correct = await LoginAsync(client, email);

        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
        Assert.Equal("identity.invalid_credentials", (await ReadAsync(correct)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Relative("/api/v1/auth/me"))).StatusCode);
    }

    [Fact]
    public async Task SignOut_EndsTheSession()
    {
        var (client, _) = await RegisteredAndSignedInAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Relative("/api/v1/auth/me"))).StatusCode);

        var logout = await PostAsync(client, "/api/v1/auth/logout", body: null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Relative("/api/v1/auth/me"))).StatusCode);
    }

    // ---- antiforgery

    [Theory]
    [InlineData("/api/v1/auth/register")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/logout")]
    public async Task AnUnsafeRequest_WithoutAnAntiforgeryToken_Is400_AndChangesNothing(string path)
    {
        var client = CreateClient();
        var email = NewEmail();

        var response = await PostAsync(client, path, new { email, password = Password, organizationName = "Soforr", currency = "GMD", timeZoneId = "UTC" }, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery.invalid", (await ReadAsync(response)).GetProperty("code").GetString());
        await using var db = _database.CreateContext();
        Assert.False(await db.Set<ApplicationUser>().AnyAsync(candidate => candidate.Email == email));
    }

    [Fact]
    public async Task ASignOut_WithATokenFetchedBeforeSigningIn_IsRejected()
    {
        var client = CreateClient();
        var email = NewEmail();
        await RegisterAsync(client, email);
        using var tokenResponse = await client.GetAsync(Relative("/api/v1/auth/antiforgery"));
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, Relative("/api/v1/auth/logout"));
        request.Headers.Add(token.RootElement.GetProperty("headerName").GetString()!, token.RootElement.GetProperty("token").GetString());
        var response = await client.SendAsync(request);

        // The token is bound to the identity it was issued for, so a token from the anonymous session is no good
        // once signed in: the client must fetch a new one after signing in.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Relative("/api/v1/auth/me"))).StatusCode);
    }

    [Fact]
    public async Task TheAntiforgeryToken_IsNotCached()
    {
        var client = CreateClient();

        var response = await client.GetAsync(Relative("/api/v1/auth/antiforgery"));

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-soforr.antiforgery=", StringComparison.Ordinal) && value.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    // ---- session lifetime and revocation

    [Fact]
    public async Task ASession_EndsAtTheAbsoluteLifetime_EvenWhileActive()
    {
        var (client, _) = await RegisteredAndSignedInAsync(new Dictionary<string, string?> { ["Session:AbsoluteLifetime"] = "00:00:03" });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Relative("/api/v1/auth/me"))).StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(3.5));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Relative("/api/v1/auth/me"))).StatusCode);
    }

    [Fact]
    public async Task ChangingTheSecurityStamp_RevokesTheSession()
    {
        var (client, email) = await RegisteredAndSignedInAsync(new Dictionary<string, string?> { ["Session:SecurityStampValidationInterval"] = "00:00:00" });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Relative("/api/v1/auth/me"))).StatusCode);

        await using (var db = _database.CreateContext())
        {
            var user = await db.Set<ApplicationUser>().SingleAsync(candidate => candidate.Email == email);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Relative("/api/v1/auth/me"))).StatusCode);
    }

    [Fact]
    public async Task ARoleChange_IsPickedUpByAnExistingSession()
    {
        var (client, email) = await RegisteredAndSignedInAsync(new Dictionary<string, string?> { ["Session:SecurityStampValidationInterval"] = "00:00:00" });

        await using (var db = _database.CreateContext())
        {
            var userId = (await db.Set<ApplicationUser>().SingleAsync(candidate => candidate.Email == email)).Id;
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Memberships\" SET \"Role\" = 'Staff' WHERE \"UserId\" = {0}", userId);
        }

        var account = await ReadAsync(await client.GetAsync(Relative("/api/v1/auth/me")));
        Assert.Equal("Staff", account.GetProperty("role").GetString());
    }
}
