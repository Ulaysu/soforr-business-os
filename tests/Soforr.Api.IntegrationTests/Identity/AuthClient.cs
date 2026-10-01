using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Identity;
using Soforr.Infrastructure.Identity;

namespace Soforr.Api.IntegrationTests.Identity;

/// <summary>
/// Helpers for tests that act as signed-in users. The client uses https because the session and antiforgery
/// cookies are Secure and a client sends them only then; every unsafe request carries a fresh antiforgery token,
/// because the token is bound to the current identity.
/// </summary>
internal static class AuthClient
{
    public const string Password = "correct horse battery staple";

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    public static Uri Relative(string path) => new(path, UriKind.Relative);

    public static WebApplicationFactory<Program> CreateFactory(string connectionString, Dictionary<string, string?>? settings = null) =>
        new ApiFactory(connectionString).WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            if (settings is not null)
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            }
        });

    /// <summary>A new client with its own cookie jar (its own "browser").</summary>
    public static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body, bool withToken = true)
    {
        using var request = new HttpRequestMessage(method, Relative(path));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (withToken)
        {
            using var tokenResponse = await client.GetAsync(Relative("/api/v1/auth/antiforgery"));
            using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
            request.Headers.Add(token.RootElement.GetProperty("headerName").GetString()!, token.RootElement.GetProperty("token").GetString());
        }

        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body, bool withToken = true) =>
        SendAsync(client, HttpMethod.Post, path, body, withToken);

    public static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string password = Password, string organizationName = "Soforr Trading", string currency = "GMD", string timeZoneId = "Europe/London") =>
        PostAsync(client, "/api/v1/auth/register", new { email, password, organizationName, currency, timeZoneId });

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password = Password) =>
        PostAsync(client, "/api/v1/auth/login", new { email, password });

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Registers a user (a new organization they own) and signs them in. Returns the user's and organization's ids.</summary>
    public static async Task<(Guid UserId, Guid OrganizationId, string Email)> RegisterAndSignInAsync(HttpClient client)
    {
        var email = NewEmail();
        var registered = await RegisterAsync(client, email);
        registered.EnsureSuccessStatusCode();
        var account = await ReadAsync(registered);
        (await LoginAsync(client, email)).EnsureSuccessStatusCode();
        return (account.GetProperty("userId").GetGuid(), account.GetProperty("organizationId").GetGuid(), email);
    }

    /// <summary>
    /// Makes a new user a member of an existing organization with the given role and signs them in. The user is
    /// registered normally and then moved by changing their membership, because members cannot be invited yet.
    /// </summary>
    public static async Task<HttpClient> SignInAsMemberAsync(WebApplicationFactory<Program> factory, PostgresDatabaseFixture database, Guid organizationId, Role role)
    {
        var client = NewClient(factory);
        var email = NewEmail();
        (await RegisterAsync(client, email)).EnsureSuccessStatusCode();

        await using (var db = database.CreateContext())
        {
            var userId = (await db.Set<ApplicationUser>().SingleAsync(user => user.Email == email)).Id;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Memberships\" SET \"OrganizationId\" = {0}, \"Role\" = {1} WHERE \"UserId\" = {2}",
                organizationId,
                role.ToString(),
                userId);
        }

        (await LoginAsync(client, email)).EnsureSuccessStatusCode();
        return client;
    }
}
