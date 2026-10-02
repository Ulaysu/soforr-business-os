using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Catalog;
using Soforr.Domain.Identity;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Catalog;

/// <summary>The Product endpoints in the running application, on a real database.</summary>
[Trait("Category", "Integration")]
public sealed class ProductApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public ProductApiTests(PostgresDatabaseFixture database)
    {
        _database = database;
        _factory = CreateFactory(database.ConnectionString);
    }

    public void Dispose()
    {
        _clients.ForEach(client => client.Dispose());
        _factory.Dispose();
    }

    private async Task<(HttpClient Client, Guid OrganizationId)> OwnerAsync()
    {
        var client = NewClient(_factory);
        _clients.Add(client);
        var (_, organizationId, _) = await RegisterAndSignInAsync(client);
        return (client, organizationId);
    }

    private static object Body(string sku = "ABC-123", string name = "Rice 25kg") =>
        new { sku, name, unitOfMeasure = "bag", sellingPrice = 125.5m, costPrice = 90.25m, reorderPoint = 12m, reorderLeadTimeDays = 7 };

    private static async Task<JsonElement> CreateAsync(HttpClient client, string sku = "ABC-123", string name = "Rice 25kg")
    {
        var response = await PostAsync(client, "/api/v1/products", Body(sku, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    // ---- create and get

    [Fact]
    public async Task Create_Returns201WithLocation_AndTheProductCanBeFetched()
    {
        var (client, organizationId) = await OwnerAsync();

        var response = await PostAsync(client, "/api/v1/products", Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadAsync(response);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/products/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("GMD", created.GetProperty("currency").GetString());

        var fetched = await ReadAsync(await client.GetAsync(Relative($"/api/v1/products/{id}")));
        Assert.Equal("ABC-123", fetched.GetProperty("sku").GetString());
        Assert.Equal(125.5m, fetched.GetProperty("sellingPrice").GetDecimal());
        Assert.Equal(90.25m, fetched.GetProperty("costPrice").GetDecimal());
        Assert.Equal(12m, fetched.GetProperty("reorderPoint").GetDecimal());
        Assert.True(fetched.GetProperty("isActive").GetBoolean());

        await using var db = _database.CreateContext(organizationId);
        Assert.Equal(1, await db.Set<Product>().CountAsync(p => p.Id == id));
    }

    [Fact]
    public async Task TheResponse_IsTheDtoShape_NotTheDomainEntity()
    {
        var (client, _) = await OwnerAsync();

        var created = await CreateAsync(client);

        var names = created.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["costPrice", "createdAtUtc", "currency", "id", "isActive", "name", "reorderLeadTimeDays", "reorderPoint", "sellingPrice", "sku", "unitOfMeasure", "updatedAtUtc"],
            names);
    }

    [Theory]
    [InlineData("", "Name", "bag", 1, 1, "sku", "product.sku_required")]
    [InlineData("SKU", "", "bag", 1, 1, "name", "product.name_required")]
    [InlineData("SKU", "Name", "", 1, 1, "unitOfMeasure", "product.unit_of_measure_required")]
    [InlineData("SKU", "Name", "bag", -1, 1, "sellingPrice", "product.selling_price_negative")]
    [InlineData("SKU", "Name", "bag", 1, -1, "costPrice", "product.cost_price_negative")]
    public async Task Create_WithInvalidInput_Returns400_WithTheFieldTheCodeAndTheCorrelationId(string sku, string name, string unit, int selling, int cost, string field, string code)
    {
        var (client, organizationId) = await OwnerAsync();

        var response = await PostAsync(client, "/api/v1/products", new { sku, name, unitOfMeasure = unit, sellingPrice = selling, costPrice = cost });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty(field).GetArrayLength());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Correlation-ID")), problem.GetProperty("correlationId").GetString());
        await using var db = _database.CreateContext(organizationId);
        Assert.Equal(0, await db.Set<Product>().CountAsync());
    }

    [Fact]
    public async Task Create_WithMissingPrices_Returns400_NotAZeroPrice()
    {
        var (client, _) = await OwnerAsync();

        var response = await PostAsync(client, "/api/v1/products", new { sku = "S", name = "N", unitOfMeasure = "bag" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("product.selling_price_required", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_WithAMalformedBody_Returns400Problem_NotAServerError()
    {
        var (client, _) = await OwnerAsync();
        using var tokenResponse = await client.GetAsync(Relative("/api/v1/auth/antiforgery"));
        var token = await ReadAsync(tokenResponse);
        using var request = new HttpRequestMessage(HttpMethod.Post, Relative("/api/v1/products")) { Content = new StringContent("{ nope", Encoding.UTF8, "application/json") };
        request.Headers.Add(token.GetProperty("headerName").GetString()!, token.GetProperty("token").GetString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("abc-123")]
    [InlineData("ABC-123")]
    public async Task Create_WithASkuThatExistsInAnyCase_Returns409_WithTheStableCode(string sku)
    {
        var (client, _) = await OwnerAsync();
        await CreateAsync(client);

        var response = await PostAsync(client, "/api/v1/products", Body(sku, "Another"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("product.sku_taken", problem.GetProperty("code").GetString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnUnknownProduct_Returns404Problem_WithTheStableCode()
    {
        var (client, _) = await OwnerAsync();

        var response = await client.GetAsync(Relative($"/api/v1/products/{Guid.CreateVersion7()}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("product.not_found", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // ---- update and deactivate

    [Fact]
    public async Task Update_ReplacesTheDetails()
    {
        var (client, _) = await OwnerAsync();
        var id = (await CreateAsync(client)).GetProperty("id").GetGuid();

        var response = await SendAsync(client, HttpMethod.Put, $"/api/v1/products/{id}", new { sku = "Abc-123", name = "Rice 50kg", unitOfMeasure = "sack", sellingPrice = 200, costPrice = 150 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadAsync(response);
        Assert.Equal("Abc-123", updated.GetProperty("sku").GetString());
        Assert.Equal("Rice 50kg", updated.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("reorderPoint").ValueKind);
    }

    [Fact]
    public async Task Update_ToAnExistingSkuInAnotherCase_Returns409_AndChangesNothing()
    {
        var (client, _) = await OwnerAsync();
        await CreateAsync(client, "ONE", "One");
        var two = (await CreateAsync(client, "TWO", "Two")).GetProperty("id").GetGuid();

        var response = await SendAsync(client, HttpMethod.Put, $"/api/v1/products/{two}", new { sku = "one", name = "Renamed", unitOfMeasure = "bag", sellingPrice = 1, costPrice = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await ReadAsync(await client.GetAsync(Relative($"/api/v1/products/{two}")));
        Assert.Equal("TWO", stored.GetProperty("sku").GetString());
        Assert.Equal("Two", stored.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Deactivate_KeepsTheProduct_ButMarksItInactive_AndKeepsItsSkuReserved()
    {
        var (client, _) = await OwnerAsync();
        var id = (await CreateAsync(client)).GetProperty("id").GetGuid();

        var response = await PostAsync(client, $"/api/v1/products/{id}/deactivate", body: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ReadAsync(response)).GetProperty("isActive").GetBoolean());
        Assert.False((await ReadAsync(await client.GetAsync(Relative($"/api/v1/products/{id}")))).GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, "/api/v1/products", Body("abc-123", "Reuse"))).StatusCode);
    }

    [Fact]
    public async Task ThereIsNoDeleteEndpoint()
    {
        var (client, _) = await OwnerAsync();
        var id = (await CreateAsync(client)).GetProperty("id").GetGuid();

        var response = await SendAsync(client, HttpMethod.Delete, $"/api/v1/products/{id}", body: null);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Relative($"/api/v1/products/{id}"))).StatusCode);
    }

    // ---- search

    [Fact]
    public async Task Search_ReturnsAPage_WithTheTotal_AndFiltersByText()
    {
        var (client, _) = await OwnerAsync();
        await CreateAsync(client, "RICE-1", "Basmati rice");
        await CreateAsync(client, "OIL-1", "Olive oil");
        await CreateAsync(client, "RICE-2", "Brown rice");

        var response = await client.GetAsync(Relative("/api/v1/products?q=RICE&page=1&pageSize=1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await ReadAsync(response);
        Assert.Equal(["Basmati rice"], page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(1, page.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Search_UsesADefaultPageSize_AndAnInvalidOneIsA400()
    {
        var (client, _) = await OwnerAsync();
        await CreateAsync(client);

        var defaults = await ReadAsync(await client.GetAsync(Relative("/api/v1/products")));
        var tooBig = await client.GetAsync(Relative("/api/v1/products?pageSize=101"));
        var notANumber = await client.GetAsync(Relative("/api/v1/products?page=abc"));

        Assert.Equal(25, defaults.GetProperty("pageSize").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        var problem = await ReadAsync(tooBig);
        Assert.Equal("pagination.page_size_invalid", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("pageSize", out _));
        Assert.Equal(HttpStatusCode.BadRequest, notANumber.StatusCode);
    }

    [Fact]
    public async Task Search_ListsInactiveProductsOnlyWhenAsked()
    {
        var (client, _) = await OwnerAsync();
        var id = (await CreateAsync(client)).GetProperty("id").GetGuid();
        await PostAsync(client, $"/api/v1/products/{id}/deactivate", body: null);

        var active = await ReadAsync(await client.GetAsync(Relative("/api/v1/products")));
        var all = await ReadAsync(await client.GetAsync(Relative("/api/v1/products?includeInactive=true")));

        Assert.Equal(0, active.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, all.GetProperty("totalCount").GetInt32());
    }

    // ---- documentation

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheProductEndpoints_AndNoDelete()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("CreateProduct", paths.GetProperty("/api/v1/products").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("SearchProducts", paths.GetProperty("/api/v1/products").GetProperty("get").GetProperty("operationId").GetString());
        var item = paths.GetProperty("/api/v1/products/{id}");
        Assert.Equal("GetProduct", item.GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("UpdateProduct", item.GetProperty("put").GetProperty("operationId").GetString());
        Assert.False(item.TryGetProperty("delete", out _));
        Assert.Equal("DeactivateProduct", paths.GetProperty("/api/v1/products/{id}/deactivate").GetProperty("post").GetProperty("operationId").GetString());
        Assert.True(paths.GetProperty("/api/v1/products").GetProperty("post").GetProperty("responses").TryGetProperty("409", out _));

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("ProductDto", schemas);
        Assert.DoesNotContain("Product", schemas);
        Assert.DoesNotContain("Money", schemas);
    }

    [Fact]
    public async Task AnUnsafeProductRequest_WithoutAnAntiforgeryToken_IsRejected_AndCreatesNothing()
    {
        var (client, organizationId) = await OwnerAsync();

        var response = await PostAsync(client, "/api/v1/products", Body(), withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery.invalid", (await ReadAsync(response)).GetProperty("code").GetString());
        await using var db = _database.CreateContext(organizationId);
        Assert.Equal(0, await db.Set<Product>().CountAsync());
    }

    [Fact]
    public async Task APriceIsInTheOrganizationsCurrency_EvenIfTheBodyNamesAnother()
    {
        var (client, _) = await OwnerAsync();

        var response = await PostAsync(client, "/api/v1/products", new { sku = "CUR", name = "Cur", unitOfMeasure = "bag", sellingPrice = 1, costPrice = 1, currency = "USD" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("GMD", (await ReadAsync(response)).GetProperty("currency").GetString());
    }

    [Fact]
    public async Task AStaffMember_SeesProductsWithoutTheirCost_OverHttp()
    {
        var (owner, organizationId) = await OwnerAsync();
        var id = (await CreateAsync(owner)).GetProperty("id").GetGuid();
        var staff = await SignInAsMemberAsync(_factory, _database, organizationId, Role.Staff);
        _clients.Add(staff);

        var single = await ReadAsync(await staff.GetAsync(Relative($"/api/v1/products/{id}")));
        var list = await ReadAsync(await staff.GetAsync(Relative("/api/v1/products")));

        Assert.Equal(JsonValueKind.Null, single.GetProperty("costPrice").ValueKind);
        Assert.Equal(125.5m, single.GetProperty("sellingPrice").GetDecimal());
        Assert.All(list.GetProperty("items").EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("costPrice").ValueKind));
    }
}
