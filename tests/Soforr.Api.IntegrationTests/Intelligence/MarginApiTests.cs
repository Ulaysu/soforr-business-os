using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Sales;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Intelligence;

/// <summary>
/// <c>GET /api/v1/intelligence/margins</c> through the running application: who may call it (it reveals costs), validation, the
/// shape of the answer and tenant isolation. The application uses the real clock here, so the sales are placed relative to now
/// and well inside the periods (5 and 15 days ago, with 10-day periods); exact boundaries are covered with a fixed clock in
/// <see cref="MarginTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MarginApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public MarginApiTests(PostgresDatabaseFixture database)
    {
        _database = database;
        _factory = CreateFactory(database.ConnectionString);
    }

    public void Dispose()
    {
        _clients.ForEach(client => client.Dispose());
        _factory.Dispose();
    }

    private async Task<(HttpClient Owner, Guid Organization)> OwnerAsync()
    {
        var client = NewClient(_factory);
        _clients.Add(client);
        var (_, organization, _) = await RegisterAndSignInAsync(client);
        return (client, organization);
    }

    private static async Task<Guid> ProductAsync(HttpClient owner, string name)
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = "SKU-" + Guid.NewGuid().ToString("N"), name, unitOfMeasure = "bag", sellingPrice = 10, costPrice = 6 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task SaleAsync(Guid organization, Guid product, decimal quantity, decimal price, decimal cost, int daysAgo)
    {
        var gmd = Currency.Create("GMD");
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Product", "bag", Quantity.Create(quantity), Money.Create(price, gmd), Money.Create(price, gmd), Money.Create(cost, gmd), null);
        var sale = Sale.Complete(Guid.CreateVersion7(), organization, gmd, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddDays(-daysAgo), [line]);
        await using var db = _database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string query = "") =>
        client.GetAsync(Relative("/api/v1/intelligence/margins" + query));

    [Fact]
    public async Task TheSummary_ReturnsThePeriodsTotalsAndLists_AsJson()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, "Rice");
        var oil = await ProductAsync(owner, "Oil");
        var free = await ProductAsync(owner, "Free");
        await SaleAsync(organization, rice, 10m, price: 10m, cost: 6m, daysAgo: 5);   // 40%
        await SaleAsync(organization, rice, 10m, price: 10m, cost: 5m, daysAgo: 15);  // 50%
        await SaleAsync(organization, oil, 10m, price: 5m, cost: 8m, daysAgo: 5);     // at a loss
        await SaleAsync(organization, free, 4m, price: 10m, cost: 0m, daysAgo: 5);    // no recorded cost

        var response = await GetAsync(owner, "?days=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(("GMD", "Europe/London"), (body.GetProperty("currency").GetString(), body.GetProperty("timeZoneId").GetString()));
        Assert.Equal(10, body.GetProperty("current").GetProperty("days").GetInt32());
        Assert.Equal(body.GetProperty("previous").GetProperty("endUtc").GetDateTimeOffset(), body.GetProperty("current").GetProperty("startUtc").GetDateTimeOffset());
        var totals = body.GetProperty("totals");
        var current = totals.GetProperty("current");
        Assert.Equal((150m, 140m, 10m, "partly_costed", 40m), (current.GetProperty("revenue").GetDecimal(), current.GetProperty("cost").GetDecimal(), current.GetProperty("grossMargin").GetDecimal(), current.GetProperty("status").GetString(), current.GetProperty("revenueWithoutCost").GetDecimal()));
        Assert.Equal(50m, totals.GetProperty("previous").GetProperty("marginPercent").GetDecimal());
        Assert.Equal(JsonValueKind.Number, totals.GetProperty("marginChangePoints").ValueKind);

        var negative = Assert.Single(body.GetProperty("negativeMargin").EnumerateArray());
        Assert.Equal(("Oil", -30m), (negative.GetProperty("name").GetString(), negative.GetProperty("current").GetProperty("grossMargin").GetDecimal()));
        var declining = Assert.Single(body.GetProperty("decliningMargin").EnumerateArray());
        Assert.Equal(("Rice", -10m), (declining.GetProperty("name").GetString(), declining.GetProperty("marginChangePoints").GetDecimal()));
        var missing = Assert.Single(body.GetProperty("missingCost").EnumerateArray());
        Assert.Equal(("Free", "cost_not_recorded", 40m), (missing.GetProperty("name").GetString(), missing.GetProperty("current").GetProperty("status").GetString(), missing.GetProperty("current").GetProperty("revenueWithoutCost").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("current").GetProperty("marginPercent").ValueKind);
    }

    [Fact]
    public async Task TheDefaults_AreThirtyDaysAndTenTopProducts_AndAnEmptyBusinessIsNotAnError()
    {
        var (owner, _) = await OwnerAsync();

        var body = await ReadAsync(await GetAsync(owner));

        Assert.Equal(30, body.GetProperty("current").GetProperty("days").GetInt32());
        Assert.Equal("no_sales", body.GetProperty("totals").GetProperty("current").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("totals").GetProperty("marginChangePoints").ValueKind);
        Assert.Empty(body.GetProperty("topProducts").EnumerateArray());
    }

    public enum Principal
    {
        Anonymous,
        Owner,
        Manager,
        Staff,
    }

    [Theory]
    [InlineData(Principal.Anonymous, HttpStatusCode.Unauthorized)]
    [InlineData(Principal.Owner, HttpStatusCode.OK)]
    [InlineData(Principal.Manager, HttpStatusCode.OK)]
    [InlineData(Principal.Staff, HttpStatusCode.Forbidden)]
    public async Task OnlyThoseWhoMaySeeCosts_MaySeeTheMargins(Principal principal, HttpStatusCode expected)
    {
        var (owner, organization) = await OwnerAsync();
        var client = principal switch
        {
            Principal.Owner => owner,
            Principal.Anonymous => NewClient(_factory),
            _ => await SignInAsMemberAsync(_factory, _database, organization, Enum.Parse<Role>(principal.ToString())),
        };
        if (principal != Principal.Owner)
        {
            _clients.Add(client);
        }

        var response = await GetAsync(client);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("?days=0", "margin_summary.days_invalid", "days")]
    [InlineData("?days=93", "margin_summary.days_invalid", "days")]
    [InlineData("?top=0", "margin_summary.top_invalid", "top")]
    [InlineData("?top=51", "margin_summary.top_invalid", "top")]
    public async Task InvalidParameters_Return400_WithTheCodeAndTheField(string query, string code, string field)
    {
        var (owner, _) = await OwnerAsync();

        var response = await GetAsync(owner, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty(field).GetArrayLength());
    }

    [Fact]
    public async Task AnotherOrganizationsSales_AreNeverInTheMargins()
    {
        var (first, firstOrganization) = await OwnerAsync();
        var (second, secondOrganization) = await OwnerAsync();
        await SaleAsync(firstOrganization, await ProductAsync(first, "Mine"), 2m, price: 10m, cost: 4m, daysAgo: 5);
        await SaleAsync(secondOrganization, await ProductAsync(second, "Theirs"), 700m, price: 10m, cost: 1m, daysAgo: 5);

        var mine = await ReadAsync(await GetAsync(first, "?days=10"));
        var theirs = await ReadAsync(await GetAsync(second, "?days=10"));

        Assert.Equal(20m, mine.GetProperty("totals").GetProperty("current").GetProperty("revenue").GetDecimal());
        Assert.Equal(["Mine"], mine.GetProperty("topProducts").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(7000m, theirs.GetProperty("totals").GetProperty("current").GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheEndpoint()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/intelligence/margins").GetProperty("get");
        Assert.Equal("GetMarginSummary", operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("400", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("MarginSummaryDto", schemas);
        Assert.DoesNotContain("SoldLine", schemas);
        Assert.DoesNotContain("MarginFigures", schemas);
    }
}
