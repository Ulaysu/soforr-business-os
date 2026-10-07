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
/// <c>GET /api/v1/intelligence/sales-summary</c> through the running application: who may call it, validation, the shape of the
/// answer and tenant isolation. The application uses the real clock here, so the sales are placed relative to now and well inside
/// the periods (5 and 15 days ago, with 10-day periods); the exact period boundaries are covered with a fixed clock in
/// <see cref="SalesSummaryTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SalesSummaryApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public SalesSummaryApiTests(PostgresDatabaseFixture database)
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

    private async Task SaleAsync(Guid organization, Guid product, decimal quantity, int daysAgo)
    {
        var price = Money.Create(10m, Currency.Create("GMD"));
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Product", "bag", Quantity.Create(quantity), price, Money.Create(10m, Currency.Create("GMD")), Money.Create(6m, Currency.Create("GMD")), null);
        var sale = Sale.Complete(Guid.CreateVersion7(), organization, Currency.Create("GMD"), Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddDays(-daysAgo), [line]);
        await using var db = _database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string query = "") =>
        client.GetAsync(Relative("/api/v1/intelligence/sales-summary" + query));

    [Fact]
    public async Task TheSummary_ReturnsThePeriodsTotalsAndLists_AsJson()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, "Rice");
        await SaleAsync(organization, rice, 6m, daysAgo: 5);
        await SaleAsync(organization, rice, 10m, daysAgo: 15);

        var response = await GetAsync(owner, "?days=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("GMD", body.GetProperty("currency").GetString());
        Assert.Equal("Europe/London", body.GetProperty("timeZoneId").GetString());
        Assert.Equal(10, body.GetProperty("current").GetProperty("days").GetInt32());
        Assert.Equal(10, body.GetProperty("previous").GetProperty("days").GetInt32());
        Assert.Equal(body.GetProperty("previous").GetProperty("endUtc").GetDateTimeOffset(), body.GetProperty("current").GetProperty("startUtc").GetDateTimeOffset());
        var totals = body.GetProperty("totals");
        Assert.Equal((60m, 100m, -40m, "ok", 1, 1), (totals.GetProperty("revenue").GetDecimal(), totals.GetProperty("previousRevenue").GetDecimal(), totals.GetProperty("revenueGrowthPercent").GetDecimal(), totals.GetProperty("growthStatus").GetString(), totals.GetProperty("saleCount").GetInt32(), totals.GetProperty("previousSaleCount").GetInt32()));
        var top = Assert.Single(body.GetProperty("topSellers").EnumerateArray());
        Assert.Equal(("Rice", 6m, 10m), (top.GetProperty("name").GetString(), top.GetProperty("quantity").GetDecimal(), top.GetProperty("previousQuantity").GetDecimal()));
        var declining = Assert.Single(body.GetProperty("decliningProducts").EnumerateArray());
        Assert.Equal(40m, declining.GetProperty("declinePercent").GetDecimal());
        var slow = body.GetProperty("slowMovers");
        Assert.True(slow.GetProperty("showsFullList").GetBoolean());
        Assert.Equal(20, slow.GetProperty("bottomPercent").GetInt32());
    }

    [Fact]
    public async Task TheDefaults_AreThirtyDaysAndTenTopSellers()
    {
        var (owner, _) = await OwnerAsync();

        var body = await ReadAsync(await GetAsync(owner));

        Assert.Equal(30, body.GetProperty("current").GetProperty("days").GetInt32());
        Assert.Equal("no_comparable_previous_sales", body.GetProperty("totals").GetProperty("growthStatus").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("totals").GetProperty("revenueGrowthPercent").ValueKind);
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
    public async Task OnlyOwnersAndManagers_MaySeeTheSummary(Principal principal, HttpStatusCode expected)
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
    [InlineData("?days=0", "sales_summary.days_invalid", "days")]
    [InlineData("?days=93", "sales_summary.days_invalid", "days")]
    [InlineData("?top=0", "sales_summary.top_invalid", "top")]
    [InlineData("?top=51", "sales_summary.top_invalid", "top")]
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
    public async Task ANonNumericParameter_Returns400()
    {
        var (owner, _) = await OwnerAsync();

        var response = await GetAsync(owner, "?days=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnotherOrganizationsSales_AreNeverInTheSummary()
    {
        var (first, firstOrganization) = await OwnerAsync();
        var (second, secondOrganization) = await OwnerAsync();
        var mine = await ProductAsync(first, "Mine");
        var theirs = await ProductAsync(second, "Theirs");
        await SaleAsync(firstOrganization, mine, 2m, daysAgo: 5);
        await SaleAsync(secondOrganization, theirs, 700m, daysAgo: 5);

        var mineBody = await ReadAsync(await GetAsync(first, "?days=10"));
        var theirBody = await ReadAsync(await GetAsync(second, "?days=10"));

        Assert.Equal(20m, mineBody.GetProperty("totals").GetProperty("revenue").GetDecimal());
        Assert.Equal(["Mine"], mineBody.GetProperty("topSellers").EnumerateArray().Select(s => s.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(7000m, theirBody.GetProperty("totals").GetProperty("revenue").GetDecimal());
        Assert.Equal(["Theirs"], theirBody.GetProperty("topSellers").EnumerateArray().Select(s => s.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheEndpoint()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/intelligence/sales-summary").GetProperty("get");
        Assert.Equal("GetSalesSummary", operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("400", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("SalesSummaryDto", schemas);
        Assert.DoesNotContain("SoldLine", schemas);
        Assert.DoesNotContain("CatalogProduct", schemas);
    }
}
