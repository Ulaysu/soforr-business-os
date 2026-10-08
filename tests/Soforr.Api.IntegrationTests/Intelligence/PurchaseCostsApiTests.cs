using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Purchasing;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Intelligence;

/// <summary>
/// <c>GET /api/v1/intelligence/purchase-costs</c> through the running application: who may call it (it reveals costs), validation, the
/// shape of the answer and tenant isolation. The application uses the real clock, so orders are placed relative to now; exact
/// boundaries are covered with a fixed clock in <see cref="PurchaseCostsTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PurchaseCostsApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public PurchaseCostsApiTests(PostgresDatabaseFixture database)
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

    private async Task<Guid> SupplierAsync(Guid organization, string name)
    {
        var supplier = Supplier.Create(Guid.CreateVersion7(), organization, name, null, DateTimeOffset.UtcNow.AddDays(-300));
        await using var db = _database.CreateContext(organization);
        db.Set<Supplier>().Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private async Task PurchaseAsync(Guid organization, Guid supplier, Guid product, decimal quantity, decimal cost, int daysAgo)
    {
        var gmd = Currency.Create("GMD");
        var placed = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        var order = PurchaseOrder.CreateDraft(Guid.CreateVersion7(), organization, supplier, gmd, null, placed.AddHours(-1));
        order.AddLine(Guid.CreateVersion7(), product, "SKU", "Product", "bag", Quantity.Create(quantity), Money.Create(cost, gmd), placed.AddMinutes(-30));
        order.Place(placed);
        await using var db = _database.CreateContext(organization);
        db.Set<PurchaseOrder>().Add(order);
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string query = "") =>
        client.GetAsync(Relative("/api/v1/intelligence/purchase-costs" + query));

    [Fact]
    public async Task TheReport_ReturnsTheCostsChangesAndVolume_AsJson()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, "Rice");
        var acme = await SupplierAsync(organization, "Acme");
        await PurchaseAsync(organization, acme, rice, 10m, 100m, daysAgo: 40);
        await PurchaseAsync(organization, acme, rice, 4m, 125m, daysAgo: 5);

        var response = await GetAsync(owner, "?days=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(("GMD", "Europe/London", 10), (body.GetProperty("currency").GetString(), body.GetProperty("timeZoneId").GetString(), body.GetProperty("period").GetProperty("days").GetInt32()));
        Assert.Equal(10m, body.GetProperty("notableChangeThresholdPercent").GetDecimal());
        Assert.Equal((1, 4m, 500m), (body.GetProperty("volume").GetProperty("orders").GetInt32(), body.GetProperty("volume").GetProperty("quantity").GetDecimal(), body.GetProperty("volume").GetProperty("spend").GetDecimal()));
        var product = Assert.Single(body.GetProperty("topProducts").EnumerateArray());
        var supplier = Assert.Single(product.GetProperty("suppliers").EnumerateArray());
        Assert.Equal(("Acme", 125m, 100m, 25m, true, "ok"), (supplier.GetProperty("supplierName").GetString(), supplier.GetProperty("latest").GetProperty("unitCost").GetDecimal(), supplier.GetProperty("previous").GetProperty("unitCost").GetDecimal(), supplier.GetProperty("changePercent").GetDecimal(), supplier.GetProperty("notable").GetBoolean(), supplier.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, product.GetProperty("supplierGap").ValueKind);
        Assert.Single(body.GetProperty("notableChanges").EnumerateArray());
    }

    [Fact]
    public async Task AnEmptyBusiness_IsNotAnError()
    {
        var (owner, _) = await OwnerAsync();

        var body = await ReadAsync(await GetAsync(owner));

        Assert.Equal(30, body.GetProperty("period").GetProperty("days").GetInt32());
        Assert.Equal(0m, body.GetProperty("volume").GetProperty("spend").GetDecimal());
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
    public async Task OnlyThoseWhoMaySeeCosts_MaySeeThePurchaseCosts(Principal principal, HttpStatusCode expected)
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
    [InlineData("?days=0", "purchase_costs.days_invalid", "days")]
    [InlineData("?days=93", "purchase_costs.days_invalid", "days")]
    [InlineData("?top=0", "purchase_costs.top_invalid", "top")]
    [InlineData("?top=51", "purchase_costs.top_invalid", "top")]
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
    public async Task AnotherOrganizationsPurchases_AreNeverInTheReport()
    {
        var (first, firstOrganization) = await OwnerAsync();
        var (second, secondOrganization) = await OwnerAsync();
        await PurchaseAsync(firstOrganization, await SupplierAsync(firstOrganization, "Mine Ltd"), await ProductAsync(first, "Mine"), 2m, 10m, daysAgo: 5);
        await PurchaseAsync(secondOrganization, await SupplierAsync(secondOrganization, "Theirs Ltd"), await ProductAsync(second, "Theirs"), 700m, 10m, daysAgo: 5);

        var mine = await ReadAsync(await GetAsync(first, "?days=10"));
        var theirs = await ReadAsync(await GetAsync(second, "?days=10"));

        Assert.Equal(20m, mine.GetProperty("volume").GetProperty("spend").GetDecimal());
        Assert.Equal(["Mine"], mine.GetProperty("topProducts").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(7000m, theirs.GetProperty("volume").GetProperty("spend").GetDecimal());
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheEndpoint()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/intelligence/purchase-costs").GetProperty("get");
        Assert.Equal("GetPurchaseCosts", operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("400", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("PurchaseCostsDto", schemas);
        Assert.DoesNotContain("PurchasedLine", schemas);
    }
}
