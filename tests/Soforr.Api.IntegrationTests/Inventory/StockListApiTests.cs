using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Identity;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Inventory;

/// <summary><c>GET /api/v1/inventory/stock</c>: the stock of a page of products in one call, for the stock screen.</summary>
[Trait("Category", "Integration")]
public sealed class StockListApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public StockListApiTests(PostgresDatabaseFixture database)
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

    private static async Task<Guid> ProductAsync(HttpClient owner, string name, string? sku = null, decimal? reorderPoint = null, decimal? stock = null)
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = sku ?? "SKU-" + Guid.NewGuid().ToString("N"), name, unitOfMeasure = "bag", sellingPrice = 10, costPrice = 5, reorderPoint, reorderLeadTimeDays = reorderPoint is null ? (int?)null : 7 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetGuid();
        if (stock is { } quantity)
        {
            Assert.Equal(HttpStatusCode.Created, (await PostAsync(owner, "/api/v1/inventory/adjustments", new { productId = id, quantityChange = quantity, note = "Opening count" })).StatusCode);
        }

        return id;
    }

    private static Task<HttpResponseMessage> ListAsync(HttpClient client, string query = "") => client.GetAsync(Relative("/api/v1/inventory/stock" + query));

    [Fact]
    public async Task ThePage_ListsTheActiveProductsByName_WithTheirStock_AndTheSharedDefinitionOfLow()
    {
        var (owner, _) = await OwnerAsync();
        await ProductAsync(owner, "Rice", reorderPoint: 5, stock: 3m);   // low
        await ProductAsync(owner, "Oil", reorderPoint: 5, stock: 20m);   // fine
        await ProductAsync(owner, "Salt");                                // never had stock
        var gone = await ProductAsync(owner, "Discontinued", stock: 1m);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(owner, $"/api/v1/products/{gone}/deactivate", null)).StatusCode);

        var response = await ListAsync(owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["Oil", "Rice", "Salt"], items.Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal([20m, 3m, 0m], items.Select(i => i.GetProperty("quantity").GetDecimal()).ToArray());
        Assert.Equal([false, true, false], items.Select(i => i.GetProperty("isLow").GetBoolean()).ToArray());
        Assert.Equal((3, 1, 25), (body.GetProperty("totalCount").GetInt32(), body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32()));
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("updatedAtUtc").ValueKind);
    }

    [Fact]
    public async Task TheFigures_AreTheSameAsGetStockLevel()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, "Rice", reorderPoint: 5, stock: 3m);

        var listed = (await ReadAsync(await ListAsync(owner))).GetProperty("items")[0];
        var single = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}")));

        Assert.Equal(single.ToString(), listed.ToString());
    }

    [Fact]
    public async Task Search_MatchesPartOfTheSkuOrName_IgnoringCase_AndPagingWorks()
    {
        var (owner, _) = await OwnerAsync();
        await ProductAsync(owner, "Rice 25kg", sku: "RICE-25");
        await ProductAsync(owner, "Rice 5kg", sku: "RICE-5");
        await ProductAsync(owner, "Cooking oil", sku: "OIL-5");

        var byName = await ReadAsync(await ListAsync(owner, "?q=RICE"));
        var bySku = await ReadAsync(await ListAsync(owner, "?q=oil-"));
        var second = await ReadAsync(await ListAsync(owner, "?page=2&pageSize=2"));

        Assert.Equal(2, byName.GetProperty("totalCount").GetInt32());
        Assert.Equal(["Cooking oil"], bySku.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal((3, 1), (second.GetProperty("totalCount").GetInt32(), second.GetProperty("items").GetArrayLength()));
    }

    [Theory]
    [InlineData("?page=0", "pagination.page_invalid")]
    [InlineData("?pageSize=0", "pagination.page_size_invalid")]
    [InlineData("?pageSize=101", "pagination.page_size_invalid")]
    public async Task OutOfRangePaging_Is400_WithTheCode(string query, string code)
    {
        var (owner, _) = await OwnerAsync();

        var response = await ListAsync(owner, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(Role.Owner, HttpStatusCode.OK)]
    [InlineData(Role.Manager, HttpStatusCode.OK)]
    [InlineData(Role.Staff, HttpStatusCode.OK)]
    public async Task AnyMemberWhoMaySeeStock_MaySeeTheList(Role role, HttpStatusCode expected)
    {
        var (_, organization) = await OwnerAsync();
        using var member = await SignInAsMemberAsync(_factory, _database, organization, role);

        Assert.Equal(expected, (await ListAsync(member)).StatusCode);
    }

    [Fact]
    public async Task AnAnonymousCaller_Gets401()
    {
        using var anonymous = NewClient(_factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ListAsync(anonymous)).StatusCode);
    }

    [Fact]
    public async Task AnotherOrganizationsProducts_AreNeverListed()
    {
        var (first, _) = await OwnerAsync();
        var (second, _) = await OwnerAsync();
        await ProductAsync(first, "Mine", stock: 2m);
        await ProductAsync(second, "Theirs", stock: 9m);

        var mine = await ReadAsync(await ListAsync(first));

        Assert.Equal(["Mine"], mine.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
    }
}
