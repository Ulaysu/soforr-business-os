using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Domain.Audit;
using Soforr.Domain.Identity;
using Soforr.Domain.Offline;
using Soforr.Domain.Sales;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Offline;

/// <summary>
/// The offline endpoints through the running application against real PostgreSQL, with the server clock under the test's control:
/// the whole synchronization journey (grant, contact, offline sale, lost-response retry, offline payment, the backdating attack),
/// authentication, antiforgery, rate limiting, tenant isolation, configuration and OpenAPI.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OfflineApiTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private sealed class TestClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }

    private WebApplicationFactory<Program> Factory(TestClock clock, params (string Key, string Value)[] settings) =>
        CreateFactory(database.ConnectionString, settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
            }));

    private static DateTimeOffset At(int hour, int minute = 0, int day = 0) => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day).AddHours(hour).AddMinutes(minute);

    private static async Task<Guid> ProductAsync(HttpClient owner)
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = "SKU-" + Guid.NewGuid().ToString("N"), name = "Rice", unitOfMeasure = "bag", sellingPrice = 10, costPrice = 6 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(owner, "/api/v1/inventory/adjustments", new { productId = id, quantityChange = 100, note = "Opening stock" })).StatusCode);
        return id;
    }

    private static async Task<Guid> CustomerAsync(HttpClient owner)
    {
        var response = await PostAsync(owner, "/api/v1/customers", new { name = "Awa Jallow", creditTermsDays = 30 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> IssueAsync(HttpClient client)
    {
        var response = await PostAsync(client, "/api/v1/offline/grants", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("grantId").GetGuid();
    }

    private static Task<HttpResponseMessage> ContactAsync(HttpClient client, Guid grant) => PostAsync(client, $"/api/v1/offline/grants/{grant}/contact", null);

    private static Task<HttpResponseMessage> SellAsync(HttpClient client, Guid product, Guid grant, DateTimeOffset occurredAt, Guid saleId, Guid? customer = null) =>
        PostAsync(client, "/api/v1/sales", new
        {
            saleId,
            customerId = customer,
            amountPaid = customer is null ? (decimal?)null : 0m,
            occurredAtUtc = occurredAt,
            offlineGrantId = grant,
            lines = new[] { new { productId = product, quantity = 2, catalogPriceShown = 10 } },
        });

    // ---- the journey

    [Fact]
    public async Task AnOfflineSaleAndPayment_SynchronizeWithTheirEventTimes_AndRetriesChangeNothing()
    {
        var clock = new TestClock(At(10));
        using var factory = Factory(clock);
        using var client = NewClient(factory);
        await RegisterAndSignInAsync(client);
        var product = await ProductAsync(client);
        var customer = await CustomerAsync(client);

        var grantResponse = await PostAsync(client, "/api/v1/offline/grants", null);
        Assert.Equal(HttpStatusCode.Created, grantResponse.StatusCode);
        var grantBody = await ReadAsync(grantResponse);
        var grant = grantBody.GetProperty("grantId").GetGuid();
        Assert.Equal(At(10), grantBody.GetProperty("issuedAtUtc").GetDateTimeOffset());
        Assert.Equal(At(10).AddHours(72), grantBody.GetProperty("expiresAtUtc").GetDateTimeOffset());
        Assert.Equal((72 * 3600, 300), (grantBody.GetProperty("windowSeconds").GetInt32(), grantBody.GetProperty("contactIntervalSeconds").GetInt32()));
        Assert.Equal($"/api/v1/offline/grants/{grant}", grantResponse.Headers.Location?.OriginalString);

        foreach (var minute in new[] { 5, 10, 15 })
        {
            clock.UtcNow = At(10, minute);
            var contact = await ContactAsync(client, grant);
            Assert.Equal(HttpStatusCode.OK, contact.StatusCode);
            Assert.Equal(At(10, minute), (await ReadAsync(contact)).GetProperty("lastContactAtUtc").GetDateTimeOffset());
        }

        // The device is offline from 10:15, sells at 11:00 and reconnects at 11:03.
        clock.UtcNow = At(11, 3);
        var saleId = Guid.CreateVersion7();
        var first = await SellAsync(client, product, grant, At(11), saleId, customer);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var sale = (await ReadAsync(first)).GetProperty("sale");
        Assert.Equal((At(11), At(11, 3), grant), (sale.GetProperty("createdAtUtc").GetDateTimeOffset(), sale.GetProperty("receivedAtUtc").GetDateTimeOffset(), sale.GetProperty("offlineGrantId").GetGuid()));
        Assert.Equal(At(11).AddDays(30), sale.GetProperty("dueAtUtc").GetDateTimeOffset());

        // The response was lost: the client retries, and pays 8 at 11:01.
        var retry = await SellAsync(client, product, grant, At(11), saleId, customer);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True((await ReadAsync(retry)).GetProperty("alreadyRecorded").GetBoolean());

        var paymentId = Guid.CreateVersion7();
        var payment = await PostAsync(client, $"/api/v1/sales/{saleId}/payments", new { paymentId, amount = 8, paidAtUtc = At(11, 1), method = "Cash", offlineGrantId = grant });
        var paymentRetry = await PostAsync(client, $"/api/v1/sales/{saleId}/payments", new { paymentId, amount = 8, paidAtUtc = At(11, 1), method = "Cash", offlineGrantId = grant });
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
        Assert.Equal(HttpStatusCode.OK, paymentRetry.StatusCode);

        var read = await ReadAsync(await client.GetAsync(Relative($"/api/v1/sales/{saleId}")));
        Assert.Equal(8m, read.GetProperty("amountPaid").GetDecimal());
        Assert.Equal(98m, (await ReadAsync(await client.GetAsync(Relative($"/api/v1/inventory/stock/{product}")))).GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task TheBackdatingAttack_IsRefusedWith409_AndNothingIsRecorded()
    {
        var clock = new TestClock(At(10));
        using var factory = Factory(clock);
        using var client = NewClient(factory);
        var (_, organization, email) = await RegisterAndSignInAsync(client);
        var product = await ProductAsync(client);
        var grant = await IssueAsync(client);
        for (var minute = 5; minute <= 120; minute += 5)
        {
            clock.UtcNow = At(10, minute);
            Assert.Equal(HttpStatusCode.OK, (await ContactAsync(client, grant)).StatusCode);
        }

        // Tuesday afternoon: the cashier signs in again (the session lasts at most 12 hours) and the queued sale is sent.
        clock.UtcNow = At(15, day: 1);
        (await LoginAsync(client, email)).EnsureSuccessStatusCode();
        var saleId = Guid.CreateVersion7();
        var attack = await SellAsync(client, product, grant, At(11), saleId);

        Assert.Equal(HttpStatusCode.Conflict, attack.StatusCode);
        Assert.Equal("offline.event_time_unverifiable", (await ReadAsync(attack)).GetProperty("code").GetString());
        await using var db = database.CreateContext(organization);
        Assert.Equal(0, await db.Set<Sale>().CountAsync());
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "offline.event_refused"));
        Assert.Equal(100m, (await ReadAsync(await client.GetAsync(Relative($"/api/v1/inventory/stock/{product}")))).GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task ErrorCodes_AndStatuses_FollowTheDocumentedTaxonomy()
    {
        var clock = new TestClock(At(10));
        using var factory = Factory(clock);
        using var client = NewClient(factory);
        await RegisterAndSignInAsync(client);
        var product = await ProductAsync(client);
        var grant = await IssueAsync(client);
        clock.UtcNow = At(12);

        var future = await SellAsync(client, product, grant, At(12).AddMinutes(5), Guid.CreateVersion7());
        var noGrant = await PostAsync(client, "/api/v1/sales", new { occurredAtUtc = At(11), lines = new[] { new { productId = product, quantity = 1, catalogPriceShown = 10 } } });
        var noPriceSeen = await PostAsync(client, "/api/v1/sales", new { occurredAtUtc = At(11), offlineGrantId = grant, lines = new[] { new { productId = product, quantity = 1 } } });
        var drift = await PostAsync(client, "/api/v1/sales", new { occurredAtUtc = At(11), offlineGrantId = grant, lines = new[] { new { productId = product, quantity = 1, catalogPriceShown = 9 } } });
        var unknown = await ContactAsync(client, Guid.CreateVersion7());

        Assert.Equal((HttpStatusCode.BadRequest, "offline.event_time_in_future"), (future.StatusCode, (await ReadAsync(future)).GetProperty("code").GetString()));
        Assert.Equal((HttpStatusCode.BadRequest, "offline.grant_required"), (noGrant.StatusCode, (await ReadAsync(noGrant)).GetProperty("code").GetString()));
        Assert.Equal((HttpStatusCode.BadRequest, "sale.catalog_price_required"), (noPriceSeen.StatusCode, (await ReadAsync(noPriceSeen)).GetProperty("code").GetString()));
        Assert.Equal((HttpStatusCode.Conflict, "sale.catalog_price_changed"), (drift.StatusCode, (await ReadAsync(drift)).GetProperty("code").GetString()));
        Assert.Equal((HttpStatusCode.NotFound, "offline.grant_not_found"), (unknown.StatusCode, (await ReadAsync(unknown)).GetProperty("code").GetString()));
    }

    [Fact]
    public async Task AContactRequest_CannotCarryATime_TheServerClockIsTheOnlySource()
    {
        var clock = new TestClock(At(10));
        using var factory = Factory(clock);
        using var client = NewClient(factory);
        var (_, organization, _) = await RegisterAndSignInAsync(client);
        var grant = await IssueAsync(client);
        clock.UtcNow = At(15);

        var response = await PostAsync(client, $"/api/v1/offline/grants/{grant}/contact", new { observedAtUtc = At(11), contactAtUtc = At(11), lastContactAtUtc = At(11) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = database.CreateContext(organization);
        var times = await db.Set<OfflineContact>().Where(c => c.GrantId == grant).Select(c => c.ObservedAtUtc).ToListAsync();
        Assert.Equal([At(10), At(15)], times.Order().ToArray());
    }

    // ---- security

    [Fact]
    public async Task TheEndpoints_NeedASession_AndAnAntiforgeryToken()
    {
        using var factory = Factory(new TestClock(At(10)));
        using var anonymous = NewClient(factory);
        using var client = NewClient(factory);
        await RegisterAndSignInAsync(client);

        var issue = await PostAsync(anonymous, "/api/v1/offline/grants", null, withToken: false);
        var contact = await PostAsync(anonymous, $"/api/v1/offline/grants/{Guid.CreateVersion7()}/contact", null, withToken: false);
        var noToken = await PostAsync(client, "/api/v1/offline/grants", null, withToken: false);

        Assert.Equal(HttpStatusCode.Unauthorized, issue.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, contact.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
    }

    [Fact]
    public async Task AGrantIsNotACredential_AnotherOrganizationsOwnerCannotUseItEvenWithItsId()
    {
        var clock = new TestClock(At(10));
        using var factory = Factory(clock);
        using var first = NewClient(factory);
        using var second = NewClient(factory);
        await RegisterAndSignInAsync(first);
        await RegisterAndSignInAsync(second);
        var secondProduct = await ProductAsync(second);
        var grant = await IssueAsync(first);
        clock.UtcNow = At(11);

        var contact = await ContactAsync(second, grant);
        var sale = await SellAsync(second, secondProduct, grant, At(10, 30), Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, contact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sale.StatusCode);
        Assert.Equal("offline.grant_not_found", (await ReadAsync(sale)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AMemberOfTheSameOrganization_CannotUseAnotherMembersGrant()
    {
        var clock = new TestClock(At(10));
        using var factory = Factory(clock);
        using var owner = NewClient(factory);
        var (_, organization, _) = await RegisterAndSignInAsync(owner);
        using var staff = await SignInAsMemberAsync(factory, database, organization, Role.Staff);
        var product = await ProductAsync(owner);
        var grant = await IssueAsync(owner);
        clock.UtcNow = At(12);

        var sale = await SellAsync(staff, product, grant, At(11), Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, sale.StatusCode);
    }

    [Fact]
    public async Task AStaffCashier_MayHoldAGrant()
    {
        using var factory = Factory(new TestClock(At(10)));
        using var owner = NewClient(factory);
        var (_, organization, _) = await RegisterAndSignInAsync(owner);
        using var staff = await SignInAsMemberAsync(factory, database, organization, Role.Staff);

        Assert.NotEqual(Guid.Empty, await IssueAsync(staff));
    }

    [Fact]
    public async Task TheOfflineEndpoints_AreRateLimited_WithTheExistingHardening()
    {
        using var factory = Factory(new TestClock(At(10)), ("Hardening:OfflinePermitsPerMinute", "3"));
        using var client = NewClient(factory);
        await RegisterAndSignInAsync(client);
        var grant = await IssueAsync(client);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last = await ContactAsync(client, grant);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses.ToArray());
        Assert.True(last!.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal("rate_limit.exceeded", (await ReadAsync(last)).GetProperty("code").GetString());
        // The rest of the API is not limited by the offline policy.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Relative("/api/v1/products"))).StatusCode);
    }

    // ---- configuration and documentation

    [Theory]
    [InlineData("Offline:Window", "00:10:00")]
    [InlineData("Offline:Window", "8.00:00:00")]
    [InlineData("Offline:MinGap", "00:05:00")]
    [InlineData("Offline:ClockSkew", "01:00:00")]
    [InlineData("Offline:ContactInterval", "00:00:10")]
    [InlineData("Offline:GrantMaxAge", "01:00:00")]
    [InlineData("Offline:EvidenceRetention", "00:01:00")]
    [InlineData("Hardening:OfflinePermitsPerMinute", "0")]
    public void ANonsensicalOfflineSetting_StopsTheApplicationAtStart(string key, string value)
    {
        using var factory = Factory(new TestClock(At(10)), (key, value));

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheOfflineEndpointsAndTheNewFields()
    {
        using var factory = Factory(new TestClock(At(10)));
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        var issue = paths.GetProperty("/api/v1/offline/grants").GetProperty("post");
        Assert.Equal("IssueOfflineGrant", issue.GetProperty("operationId").GetString());
        Assert.True(issue.GetProperty("responses").TryGetProperty("429", out _));
        Assert.Contains("identifier, not a secret", issue.GetProperty("summary").GetString());
        var contact = paths.GetProperty("/api/v1/offline/grants/{id}/contact").GetProperty("post");
        Assert.Equal("RecordOfflineContact", contact.GetProperty("operationId").GetString());
        Assert.True(contact.GetProperty("responses").TryGetProperty("404", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.GetProperty("SaleRequest").GetProperty("properties").TryGetProperty("occurredAtUtc", out _));
        Assert.True(schemas.GetProperty("SaleRequest").GetProperty("properties").TryGetProperty("offlineGrantId", out _));
        Assert.True(schemas.GetProperty("SaleLineRequest").GetProperty("properties").TryGetProperty("catalogPriceShown", out _));
        Assert.True(schemas.GetProperty("PaymentRequest").GetProperty("properties").TryGetProperty("offlineGrantId", out _));
        Assert.True(schemas.GetProperty("SaleDto").GetProperty("properties").TryGetProperty("receivedAtUtc", out _));
        Assert.Contains("never dated at server time", paths.GetProperty("/api/v1/sales").GetProperty("post").GetProperty("summary").GetString());
    }
}
