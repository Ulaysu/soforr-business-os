using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Audit;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Inventory;

/// <summary>
/// The Inventory endpoints through the running application: the adjustment, the three queries, the authorization
/// matrix, tenant isolation, and that every change is traceable (a movement and an audit event) and reconciles.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InventoryApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public InventoryApiTests(PostgresDatabaseFixture database)
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

    private static async Task<Guid> CreateProductAsync(HttpClient owner, string name = "Rice", decimal? reorderPoint = null)
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = "SKU-" + Guid.NewGuid().ToString("N"), name, unitOfMeasure = "bag", sellingPrice = 10, costPrice = 5, reorderPoint, reorderLeadTimeDays = reorderPoint is null ? (int?)null : 7 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> AdjustAsync(HttpClient client, Guid product, decimal? change, string? note = "Counted the shelf", Guid? adjustmentId = null) =>
        PostAsync(client, "/api/v1/inventory/adjustments", new { productId = product, quantityChange = change, note, adjustmentId });

    // ---- the adjustment

    [Fact]
    public async Task AnAdjustment_Returns201_ChangesTheStock_AndLeavesAMovementAndAnAuditEvent()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await CreateProductAsync(owner);

        var response = await AdjustAsync(owner, product, 12.5m, "Opening count");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/v1/inventory/stock/{product}/movements", response.Headers.Location?.OriginalString);
        var body = await ReadAsync(response);
        Assert.Equal(12.5m, body.GetProperty("newQuantity").GetDecimal());
        Assert.False(body.GetProperty("alreadyRecorded").GetBoolean());

        var stock = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}")));
        Assert.Equal(12.5m, stock.GetProperty("quantity").GetDecimal());

        var history = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}/movements")));
        var movement = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal(body.GetProperty("movementId").GetGuid(), movement.GetProperty("id").GetGuid());
        Assert.Equal("ManualAdjustment", movement.GetProperty("reason").GetString());
        Assert.Equal("Opening count", movement.GetProperty("note").GetString());

        var trail = await ReadAsync(await owner.GetAsync(Relative("/api/v1/audit-events?action=inventory.stock_adjusted")));
        var auditItem = Assert.Single(trail.GetProperty("items").EnumerateArray());
        Assert.Equal(body.GetProperty("movementId").GetGuid(), auditItem.GetProperty("entityId").GetGuid());
        Assert.Equal("InventoryMovement", auditItem.GetProperty("entityType").GetString());

        Assert.DoesNotContain(product, await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "inventory.stock_adjusted"));
    }

    [Fact]
    public async Task RepeatingAnAdjustmentWithTheSameId_Returns200_AndChangesNothing()
    {
        var (owner, _) = await OwnerAsync();
        var product = await CreateProductAsync(owner);
        var id = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await AdjustAsync(owner, product, 5m, adjustmentId: id)).StatusCode);

        var again = await AdjustAsync(owner, product, 5m, adjustmentId: id);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await ReadAsync(again);
        Assert.True(body.GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(5m, body.GetProperty("newQuantity").GetDecimal());
        var stock = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}")));
        Assert.Equal(5m, stock.GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task RemovingMoreThanThereIs_Returns422_WithTheStableCode_AndChangesNothing()
    {
        var (owner, _) = await OwnerAsync();
        var product = await CreateProductAsync(owner);
        await AdjustAsync(owner, product, 2m);

        var response = await AdjustAsync(owner, product, -3m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync(response);
        Assert.Equal("stock_level.insufficient_stock", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("correlationId", out _));
        var stock = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}")));
        Assert.Equal(2m, stock.GetProperty("quantity").GetDecimal());
    }

    [Theory]
    [InlineData(null, "Counted", "quantityChange", "inventory.quantity_change_required")]
    [InlineData(0, "Counted", "quantityChange", "quantity_delta.zero")]
    [InlineData(1, null, "note", "inventory_movement.note_required")]
    [InlineData(1, "  ", "note", "inventory_movement.note_required")]
    public async Task InvalidAdjustments_Return400_WithTheFieldTheCodeAndTheCorrelationId(int? change, string? note, string field, string code)
    {
        var (owner, _) = await OwnerAsync();
        var product = await CreateProductAsync(owner);

        var response = await AdjustAsync(owner, product, change, note);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty(field).GetArrayLength());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Correlation-ID")), problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task AnAdjustmentOfAnUnknownProduct_Returns404()
    {
        var (owner, _) = await OwnerAsync();

        var response = await AdjustAsync(owner, Guid.CreateVersion7(), 1m);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("product.not_found", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnAdjustmentWithoutAnAntiforgeryToken_IsRejected_AndChangesNothing()
    {
        var (owner, _) = await OwnerAsync();
        var product = await CreateProductAsync(owner);

        var response = await PostAsync(owner, "/api/v1/inventory/adjustments", new { productId = product, quantityChange = 5, note = "x" }, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery.invalid", (await ReadAsync(response)).GetProperty("code").GetString());
        var stock = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}")));
        Assert.Equal(0m, stock.GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task ThereIsNoWayToSetAQuantity_OrToChangeOrDeleteAMovement()
    {
        var (owner, _) = await OwnerAsync();
        var product = await CreateProductAsync(owner);
        var adjusted = await ReadAsync(await AdjustAsync(owner, product, 3m));
        var movement = adjusted.GetProperty("movementId").GetGuid();

        var attempts = new[]
        {
            await SendAsync(owner, HttpMethod.Put, $"/api/v1/inventory/stock/{product}", new { quantity = 100 }),
            await SendAsync(owner, HttpMethod.Post, $"/api/v1/inventory/stock/{product}", new { quantity = 100 }),
            await SendAsync(owner, HttpMethod.Put, $"/api/v1/inventory/movements/{movement}", new { note = "edited" }),
            await SendAsync(owner, HttpMethod.Delete, $"/api/v1/inventory/movements/{movement}", body: null),
            await SendAsync(owner, HttpMethod.Delete, $"/api/v1/inventory/stock/{product}/movements", body: null),
        };

        Assert.All(attempts, response => Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed));
        var stock = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}")));
        Assert.Equal(3m, stock.GetProperty("quantity").GetDecimal());
    }

    // ---- low stock

    [Fact]
    public async Task TheLowStockList_ShowsProductsAtOrBelowTheirReorderPoint_AndMatchesTheStockLevel()
    {
        var (owner, _) = await OwnerAsync();
        var low = await CreateProductAsync(owner, "Low rice", reorderPoint: 10m);
        var fine = await CreateProductAsync(owner, "Fine oil", reorderPoint: 10m);
        await AdjustAsync(owner, low, 4m);
        await AdjustAsync(owner, fine, 50m);

        var list = await ReadAsync(await owner.GetAsync(Relative("/api/v1/inventory/low-stock")));

        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(low, item.GetProperty("productId").GetGuid());
        Assert.Equal(4m, item.GetProperty("quantity").GetDecimal());
        Assert.Equal(10m, item.GetProperty("reorderPoint").GetDecimal());
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        Assert.True((await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{low}")))).GetProperty("isLow").GetBoolean());
        Assert.False((await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{fine}")))).GetProperty("isLow").GetBoolean());
    }

    [Fact]
    public async Task InvalidPaging_Returns400_ForTheLowStockListAndTheHistory()
    {
        var (owner, _) = await OwnerAsync();
        var product = await CreateProductAsync(owner);

        var low = await owner.GetAsync(Relative("/api/v1/inventory/low-stock?pageSize=500"));
        var history = await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}/movements?page=0"));
        var notANumber = await owner.GetAsync(Relative("/api/v1/inventory/low-stock?page=abc"));

        Assert.Equal(HttpStatusCode.BadRequest, low.StatusCode);
        Assert.Equal("pagination.page_size_invalid", (await ReadAsync(low)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, history.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notANumber.StatusCode);
    }

    // ---- authorization matrix

    public enum Operation
    {
        Adjust,
        GetStock,
        LowStock,
        History,
    }

    private static async Task<HttpResponseMessage> PerformAsync(HttpClient client, Operation operation, Guid product) => operation switch
    {
        Operation.Adjust => await AdjustAsync(client, product, 1m),
        Operation.GetStock => await client.GetAsync(Relative($"/api/v1/inventory/stock/{product}")),
        Operation.LowStock => await client.GetAsync(Relative("/api/v1/inventory/low-stock")),
        Operation.History => await client.GetAsync(Relative($"/api/v1/inventory/stock/{product}/movements")),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    public static TheoryData<string, Operation, HttpStatusCode> Matrix()
    {
        var data = new TheoryData<string, Operation, HttpStatusCode>();
        foreach (var operation in Enum.GetValues<Operation>())
        {
            data.Add("Anonymous", operation, HttpStatusCode.Unauthorized);
            data.Add("Owner", operation, operation == Operation.Adjust ? HttpStatusCode.Created : HttpStatusCode.OK);
            data.Add("Manager", operation, operation == Operation.Adjust ? HttpStatusCode.Created : HttpStatusCode.OK);
        }

        data.Add("Staff", Operation.GetStock, HttpStatusCode.OK);
        data.Add("Staff", Operation.LowStock, HttpStatusCode.OK);
        data.Add("Staff", Operation.History, HttpStatusCode.Forbidden);
        data.Add("Staff", Operation.Adjust, HttpStatusCode.Forbidden);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryOperation_IsAllowedOrDenied_AsTheMatrixSays(string principal, Operation operation, HttpStatusCode expected)
    {
        var (owner, organization) = await OwnerAsync();
        var product = await CreateProductAsync(owner);
        HttpClient client;
        if (principal == "Anonymous")
        {
            client = NewClient(_factory);
            _clients.Add(client);
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

        var response = await PerformAsync(client, operation, product);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ADeniedAdjustment_ChangesNothing_AndWritesNoAuditEvent()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await CreateProductAsync(owner);
        var staff = await SignInAsMemberAsync(_factory, _database, organization, Role.Staff);
        _clients.Add(staff);

        var denied = await AdjustAsync(staff, product, 5m);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("identity.forbidden", (await ReadAsync(denied)).GetProperty("code").GetString());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<InventoryMovement>().CountAsync());
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == "inventory.stock_adjusted"));
    }

    // ---- tenant isolation

    [Theory]
    [InlineData(Operation.Adjust)]
    [InlineData(Operation.GetStock)]
    [InlineData(Operation.History)]
    public async Task AnotherOrganizationsProduct_IsNotFound_AndIsNeverChanged(Operation operation)
    {
        var (owner, _) = await OwnerAsync();
        var product = await CreateProductAsync(owner);
        await AdjustAsync(owner, product, 7m);
        var other = NewClient(_factory);
        _clients.Add(other);
        await RegisterAndSignInAsync(other);

        var response = await PerformAsync(other, operation, product);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("product.not_found", (await ReadAsync(response)).GetProperty("code").GetString());
        var stock = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}")));
        Assert.Equal(7m, stock.GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task TheLowStockList_NeverShowsAnotherOrganizationsProducts()
    {
        var (first, _) = await OwnerAsync();
        var (second, _) = await OwnerAsync();
        await CreateProductAsync(first, "First's", reorderPoint: 5m);
        await CreateProductAsync(second, "Second's", reorderPoint: 5m);

        var mine = await ReadAsync(await first.GetAsync(Relative("/api/v1/inventory/low-stock")));
        var theirs = await ReadAsync(await second.GetAsync(Relative("/api/v1/inventory/low-stock")));

        Assert.Equal(["First's"], mine.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(["Second's"], theirs.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
    }

    // ---- reconciliation and documentation

    [Fact]
    public async Task AfterAMixOfAdjustments_TheStockEqualsTheSumOfTheMovements()
    {
        var (owner, _) = await OwnerAsync();
        var product = await CreateProductAsync(owner);

        foreach (var change in new[] { 10m, -3m, 4.5m, -0.5m, -100m, 2m })
        {
            await AdjustAsync(owner, product, change);
        }

        var stock = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}")));
        Assert.Equal(13m, stock.GetProperty("quantity").GetDecimal());
        Assert.DoesNotContain(product, await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheInventoryEndpoints()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        var adjust = paths.GetProperty("/api/v1/inventory/adjustments").GetProperty("post");
        Assert.Equal("AdjustInventory", adjust.GetProperty("operationId").GetString());
        Assert.True(adjust.GetProperty("responses").TryGetProperty("422", out _));
        Assert.True(adjust.GetProperty("responses").TryGetProperty("409", out _));
        Assert.Equal("GetStockLevel", paths.GetProperty("/api/v1/inventory/stock/{productId}").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("ListLowStock", paths.GetProperty("/api/v1/inventory/low-stock").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("GetMovementHistory", paths.GetProperty("/api/v1/inventory/stock/{productId}/movements").GetProperty("get").GetProperty("operationId").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("StockLevelDto", schemas);
        Assert.DoesNotContain("StockLevel", schemas);
        Assert.DoesNotContain("InventoryMovement", schemas);
    }
}
