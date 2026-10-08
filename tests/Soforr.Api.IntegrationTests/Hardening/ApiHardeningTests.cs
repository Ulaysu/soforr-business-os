using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Soforr.Api.IntegrationTests.Hardening;

/// <summary>
/// SECURITY-002: rate limiting (strict on authentication), the request size limit, security headers, HSTS and the CORS allowlist.
/// None of these tests needs a database: a limit, a header or a refused origin is decided before any handler runs.
/// </summary>
public sealed class ApiHardeningTests
{
    private static WebApplicationFactory<Program> Factory(string environment = "Development", params (string Key, string Value)[] settings) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)));
        });

    private static Uri Path(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    // ---- rate limiting

    [Fact]
    public async Task TheAuthenticationEndpoints_AreLimitedPerClient_With429AndRetryAfter()
    {
        using var factory = Factory(settings: ("Hardening:AuthPermitsPerMinute", "3"));
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            last = await client.PostAsJsonAsync(Path("/api/v1/auth/login"), new { email = "a@example.test", password = "x" });
            statuses.Add(last.StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(3));
        Assert.Equal([HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses.Skip(3).ToArray());
        Assert.True(last!.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var problem = await ReadAsync(last);
        Assert.Equal(("rate_limit.exceeded", 429), (problem.GetProperty("code").GetString(), problem.GetProperty("status").GetInt32()));
        Assert.True(problem.TryGetProperty("correlationId", out _));
        Assert.True(last.Headers.Contains("X-Content-Type-Options")); // the headers are on the 429 too
    }

    [Fact]
    public async Task EveryAuthenticationEndpoint_SharesTheSameLimit()
    {
        using var factory = Factory(settings: ("Hardening:AuthPermitsPerMinute", "2"));
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync(Path("/api/v1/auth/login"), new { });
        await client.PostAsJsonAsync(Path("/api/v1/auth/register"), new { });
        var third = await client.PostAsJsonAsync(Path("/api/v1/auth/logout"), new { });

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task TheAuthenticationLimit_DoesNotLimitTheRestOfTheApi()
    {
        using var factory = Factory(settings: ("Hardening:AuthPermitsPerMinute", "1"));
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            statuses.Add((await client.GetAsync(Path("/api/v1/products"))).StatusCode);
        }

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Unauthorized, status));
    }

    [Fact]
    public async Task TheGlobalLimit_Applies_ButNeverToTheHealthChecks()
    {
        using var factory = Factory(settings: ("Hardening:GlobalPermitsPerMinute", "4"));
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            statuses.Add((await client.GetAsync(Path("/api/v1/products"))).StatusCode);
        }

        Assert.Equal([HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses.Skip(4).ToArray());
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path("/health/live"))).StatusCode);
        }
    }

    // ---- request size

    [Fact]
    public async Task ARequestBiggerThanTheLimit_Is413_BeforeItIsRead_AndASmallOneIsNot()
    {
        using var factory = Factory(settings: ("Hardening:MaxRequestBodyBytes", "100"));
        using var client = factory.CreateClient();

        using var big = new StringContent(new string('x', 500), Encoding.UTF8, "application/json");
        using var small = new StringContent("{}", Encoding.UTF8, "application/json");
        var tooLarge = await client.PostAsync(Path("/api/v1/products"), big);
        var fine = await client.PostAsync(Path("/api/v1/products"), small);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        var problem = await ReadAsync(tooLarge);
        Assert.Equal("request.too_large", problem.GetProperty("code").GetString());
        Assert.Contains("100 bytes", problem.GetProperty("detail").GetString());
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, fine.StatusCode);
    }

    [Fact]
    public async Task TheDefaultRequestLimit_IsOneMebibyte()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var big = new StringContent(new string('x', 1_048_577), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(Path("/api/v1/products"), big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // ---- headers

    [Theory]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/api/v1/products", HttpStatusCode.Unauthorized)]
    [InlineData("/nothing/here", HttpStatusCode.NotFound)]
    public async Task EveryResponse_CarriesTheSecurityHeaders(string path, HttpStatusCode expected)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Path(path));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("camera=()", response.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task ApiResponses_AreNotCacheable()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var api = await client.GetAsync(Path("/api/v1/products"));

        Assert.True(api.Headers.CacheControl?.NoStore);
    }

    // ---- HSTS

    [Fact]
    public async Task OutsideDevelopment_HttpsResponsesCarryHsts_ForTheConfiguredDays()
    {
        using var factory = Factory("Production", ("Hardening:HstsDays", "30"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://api.example.test"), AllowAutoRedirect = false });

        var response = await client.GetAsync(Path("/health/live"));

        var hsts = response.Headers.GetValues("Strict-Transport-Security").Single();
        Assert.Contains("max-age=2592000", hsts);
        Assert.Contains("includeSubDomains", hsts);
    }

    [Fact]
    public async Task InDevelopment_NoHstsIsSent()
    {
        using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://api.example.test"), AllowAutoRedirect = false });

        var response = await client.GetAsync(Path("/health/live"));

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    // ---- CORS

    private static async Task<HttpResponseMessage> WithOriginAsync(HttpClient client, string origin, HttpMethod? method = null, string path = "/health/live")
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, Path(path));
        request.Headers.Add("Origin", origin);
        if (method == HttpMethod.Options)
        {
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ByDefault_NoOriginIsAllowed_NotEvenOnAPreflight()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var simple = await WithOriginAsync(client, "https://evil.example");
        var preflight = await WithOriginAsync(client, "https://evil.example", HttpMethod.Options, "/api/v1/sales");

        Assert.False(simple.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task OnlyAnAllowlistedOrigin_IsAllowed_WithCredentials_AndOnlyTheNeededMethodsAndHeaders()
    {
        using var factory = Factory(settings: ("Hardening:CorsAllowedOrigins:0", "https://app.example.com"));
        using var client = factory.CreateClient();

        var allowed = await WithOriginAsync(client, "https://app.example.com");
        var other = await WithOriginAsync(client, "https://evil.example");
        var lookalike = await WithOriginAsync(client, "https://app.example.com.evil.example");
        var preflight = await WithOriginAsync(client, "https://app.example.com", HttpMethod.Options, "/api/v1/sales");

        Assert.Equal("https://app.example.com", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(lookalike.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("https://app.example.com", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var methods = string.Join(",", preflight.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.Contains("POST", methods);
        Assert.DoesNotContain("*", methods);
        Assert.DoesNotContain("*", string.Join(",", preflight.Headers.GetValues("Access-Control-Allow-Headers")));
    }

    // ---- configuration is validated at start

    [Theory]
    [InlineData("Hardening:CorsAllowedOrigins:0", "*")]
    [InlineData("Hardening:CorsAllowedOrigins:0", "https://app.example.com/")]
    [InlineData("Hardening:CorsAllowedOrigins:0", "https://app.example.com/path")]
    [InlineData("Hardening:CorsAllowedOrigins:0", "app.example.com")]
    [InlineData("Hardening:CorsAllowedOrigins:0", "ftp://app.example.com")]
    [InlineData("Hardening:AuthPermitsPerMinute", "0")]
    [InlineData("Hardening:GlobalPermitsPerMinute", "-5")]
    [InlineData("Hardening:MaxRequestBodyBytes", "0")]
    [InlineData("Hardening:HstsDays", "0")]
    public void ANonsensicalSetting_StopsTheApplicationAtStart(string key, string value)
    {
        using var factory = Factory(settings: (key, value));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Hardening", failure.ToString());
    }
}
