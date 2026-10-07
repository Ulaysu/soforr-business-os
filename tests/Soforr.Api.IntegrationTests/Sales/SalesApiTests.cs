using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Audit;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Sales;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Sales;

/// <summary>
/// The Sales endpoints through the running application against real PostgreSQL: recording a sale (stock, snapshots,
/// audit), refusing what would take too much stock, repeating a sale id (same or different), voiding exactly once,
/// concurrent sales of the last unit, the authorization matrix, tenant isolation and cost visibility.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SalesApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public SalesApiTests(PostgresDatabaseFixture database)
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

    private static async Task<Guid> ProductAsync(HttpClient owner, string name = "Rice", decimal price = 10m, decimal cost = 6m, decimal? stock = null)
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = "SKU-" + Guid.NewGuid().ToString("N"), name, unitOfMeasure = "bag", sellingPrice = price, costPrice = cost });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetGuid();
        if (stock is { } quantity)
        {
            Assert.Equal(HttpStatusCode.Created, (await PostAsync(owner, "/api/v1/inventory/adjustments", new { productId = id, quantityChange = quantity, note = "Opening stock" })).StatusCode);
        }

        return id;
    }

    private static Task<HttpResponseMessage> SellAsync(HttpClient client, Guid? saleId, params (Guid? Product, decimal? Quantity)[] lines) =>
        PostAsync(client, "/api/v1/sales", new { saleId, lines = lines.Select(l => new { productId = l.Product, quantity = l.Quantity }).ToArray() });

    private static Task<HttpResponseMessage> VoidAsync(HttpClient client, Guid sale, string? reason = "Customer changed their mind") =>
        PostAsync(client, $"/api/v1/sales/{sale}/void", new { reason });

    private static async Task<decimal> StockAsync(HttpClient client, Guid product) =>
        (await ReadAsync(await client.GetAsync(Relative($"/api/v1/inventory/stock/{product}")))).GetProperty("quantity").GetDecimal();

    private static JsonElement LineOf(JsonElement sale, Guid product) =>
        sale.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("productId").GetGuid() == product);

    // ---- recording a sale

    [Fact]
    public async Task ASale_RemovesTheStock_RecordsSnapshotsAndTotals_AndLeavesMovementsAndAnAuditEvent()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, "Rice", price: 12.5m, cost: 7m, stock: 20m);
        var oil = await ProductAsync(owner, "Oil", price: 99.99m, cost: 80m, stock: 5m);

        var response = await SellAsync(owner, null, (rice, 4m), (oil, 2m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        var saleId = body.GetProperty("saleId").GetGuid();
        Assert.Equal($"/api/v1/sales/{saleId}", response.Headers.Location?.OriginalString);
        Assert.False(body.GetProperty("alreadyRecorded").GetBoolean());
        var sale = body.GetProperty("sale");
        Assert.Equal("Completed", sale.GetProperty("status").GetString());
        Assert.Equal("GMD", sale.GetProperty("currency").GetString());
        Assert.Equal(249.98m, sale.GetProperty("total").GetDecimal());
        Assert.Equal(0m, sale.GetProperty("totalDiscount").GetDecimal());
        Assert.Equal(188m, sale.GetProperty("totalCost").GetDecimal());
        Assert.Equal(["Oil", "Rice"], sale.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("name").GetString()!).ToArray());
        var riceLine = LineOf(sale, rice);
        Assert.Equal((4m, 12.5m, 12.5m, 50m, 7m), (riceLine.GetProperty("quantity").GetDecimal(), riceLine.GetProperty("catalogUnitPrice").GetDecimal(), riceLine.GetProperty("unitPrice").GetDecimal(), riceLine.GetProperty("lineTotal").GetDecimal(), riceLine.GetProperty("costPrice").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, riceLine.GetProperty("priceOverrideReason").ValueKind);

        Assert.Equal(16m, await StockAsync(owner, rice));
        Assert.Equal(3m, await StockAsync(owner, oil));
        var history = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{rice}/movements")));
        var movement = history.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("reason").GetString() == "SaleCompleted");
        Assert.Equal(-4m, movement.GetProperty("quantityChange").GetDecimal());
        Assert.Equal("Sale", movement.GetProperty("sourceType").GetString());
        Assert.Equal(saleId, movement.GetProperty("sourceId").GetGuid());

        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.sale_created" && e.EntityId == saleId));
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));

        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/sales/{saleId}")));
        Assert.Equal(249.98m, read.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task ChangingAProductAfterTheSale_DoesNotChangeTheRecordedSale()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, "Rice", price: 10m, cost: 6m, stock: 10m);
        var saleId = (await ReadAsync(await SellAsync(owner, null, (product, 2m)))).GetProperty("saleId").GetGuid();

        var update = await SendAsync(owner, HttpMethod.Put, $"/api/v1/products/{product}", new { sku = "NEW-SKU", name = "Renamed", unitOfMeasure = "kg", sellingPrice = 99, costPrice = 88 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var line = LineOf(await ReadAsync(await owner.GetAsync(Relative($"/api/v1/sales/{saleId}"))), product);
        Assert.Equal(("Rice", "bag", 10m, 6m), (line.GetProperty("name").GetString(), line.GetProperty("unitOfMeasure").GetString(), line.GetProperty("unitPrice").GetDecimal(), line.GetProperty("costPrice").GetDecimal()));
    }

    [Fact]
    public async Task APriceOrCurrencyOrTotalInTheRequest_IsNotHonored()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, price: 10m, stock: 10m);

        var response = await PostAsync(owner, "/api/v1/sales", new { currency = "USD", total = 1, lines = new[] { new { productId = product, quantity = 2, unitPrice = 1, price = 1, currency = "EUR" } } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = (await ReadAsync(response)).GetProperty("sale");
        Assert.Equal("GMD", sale.GetProperty("currency").GetString());
        Assert.Equal(20m, sale.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task SellingMoreThanIsInStock_Returns422_AndChangesNothing()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, "Rice", stock: 5m);

        var response = await SellAsync(owner, null, (rice, 6m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("stock_level.insufficient_stock", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(5m, await StockAsync(owner, rice));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<Sale>().CountAsync());
        Assert.Equal(1, await db.Set<InventoryMovement>().CountAsync());
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.sale_created"));
    }

    [Fact]
    public async Task ASaleWhereOneLineIsShort_IsRefusedAsAWhole_AndNoLineTakesStock()
    {
        var (owner, organization) = await OwnerAsync();
        var plenty = await ProductAsync(owner, "Plenty", stock: 50m);
        var short1 = await ProductAsync(owner, "Short", stock: 1m);

        var response = await SellAsync(owner, null, (plenty, 10m), (short1, 2m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(50m, await StockAsync(owner, plenty));
        Assert.Equal(1m, await StockAsync(owner, short1));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<Sale>().CountAsync());
        Assert.Equal(0, await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.SaleCompleted));
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));
    }

    [Fact]
    public async Task SellingAProductThatNeverHadStock_Returns422()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);

        var response = await SellAsync(owner, null, (product, 1m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0m, await StockAsync(owner, product));
    }

    public static TheoryData<string, string> InvalidSales() => new()
    {
        { "no lines", "sale.no_lines" },
        { "no product", "sale.line_product_required" },
        { "no quantity", "sale.line_quantity_required" },
        { "zero quantity", "sale.line_quantity_required" },
        { "negative quantity", "quantity.negative" },
        { "too many decimals", "quantity.scale_exceeded" },
        { "duplicate product", "sale.line_product_duplicate" },
        { "empty sale id", "sale.sale_id_invalid" },
    };

    [Theory]
    [MemberData(nameof(InvalidSales))]
    public async Task AnInvalidSale_Returns400_WithAStableCode_AndChangesNothing(string scenario, string code)
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 10m);

        var response = scenario switch
        {
            "no lines" => await SellAsync(owner, null),
            "no product" => await SellAsync(owner, null, (null, 1m)),
            "no quantity" => await SellAsync(owner, null, (product, null)),
            "zero quantity" => await SellAsync(owner, null, (product, 0m)),
            "negative quantity" => await SellAsync(owner, null, (product, -1m)),
            "too many decimals" => await SellAsync(owner, null, (product, 1.00001m)),
            "duplicate product" => await SellAsync(owner, null, (product, 1m), (product, 2m)),
            "empty sale id" => await SellAsync(owner, Guid.Empty, (product, 1m)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(10m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<Sale>().CountAsync());
    }

    [Fact]
    public async Task AnUnknownOrInactiveProduct_IsRefused()
    {
        var (owner, _) = await OwnerAsync();
        var inactive = await ProductAsync(owner, stock: 5m);
        Assert.True((await PostAsync(owner, $"/api/v1/products/{inactive}/deactivate", null)).IsSuccessStatusCode);

        var unknown = await SellAsync(owner, null, (Guid.CreateVersion7(), 1m));
        var notActive = await SellAsync(owner, null, (inactive, 1m));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("product.not_found", (await ReadAsync(unknown)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notActive.StatusCode);
        Assert.Equal("sale.product_inactive", (await ReadAsync(notActive)).GetProperty("code").GetString());
        Assert.Equal(5m, await StockAsync(owner, inactive));
    }

    // ---- the sale id: repeat or conflict

    private async Task<(HttpClient Owner, Guid Organization, Guid SaleId, Guid Rice, Guid Oil)> SaleOfFiveRiceAsync()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, "Rice", stock: 20m);
        var oil = await ProductAsync(owner, "Oil", stock: 20m);
        var saleId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await SellAsync(owner, saleId, (rice, 5m))).StatusCode);
        return (owner, organization, saleId, rice, oil);
    }

    private async Task AssertSaleUnchangedAsync(HttpClient owner, Guid organization, Guid rice, Guid oil)
    {
        Assert.Equal(15m, await StockAsync(owner, rice));
        Assert.Equal(20m, await StockAsync(owner, oil));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<Sale>().CountAsync());
        Assert.Equal(1, await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.SaleCompleted));
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.sale_created"));
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));
    }

    [Fact]
    public async Task TheSameSaleIdWithTheSameProductsAndQuantities_Returns200_AlreadyRecorded_AndChangesNothing()
    {
        var (owner, organization, saleId, rice, oil) = await SaleOfFiveRiceAsync();

        var again = await SellAsync(owner, saleId, (rice, 5m));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await ReadAsync(again);
        Assert.True(body.GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(saleId, body.GetProperty("sale").GetProperty("id").GetGuid());
        await AssertSaleUnchangedAsync(owner, organization, rice, oil);
    }

    [Fact]
    public async Task TheSameSaleIdAfterTheCatalogPriceChanged_IsStillTheSameSale()
    {
        var (owner, organization, saleId, rice, oil) = await SaleOfFiveRiceAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(owner, HttpMethod.Put, $"/api/v1/products/{rice}", new { sku = "NEW-" + Guid.NewGuid().ToString("N"), name = "Rice", unitOfMeasure = "bag", sellingPrice = 77, costPrice = 50 })).StatusCode);

        var again = await SellAsync(owner, saleId, (rice, 5m));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(10m, LineOf((await ReadAsync(again)).GetProperty("sale"), rice).GetProperty("unitPrice").GetDecimal());
        await AssertSaleUnchangedAsync(owner, organization, rice, oil);
    }

    [Fact]
    public async Task TheSameSaleIdWithADifferentQuantity_Returns409_AndChangesNothing()
    {
        var (owner, organization, saleId, rice, oil) = await SaleOfFiveRiceAsync();

        var response = await SellAsync(owner, saleId, (rice, 8m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("sale.id_conflict", (await ReadAsync(response)).GetProperty("code").GetString());
        await AssertSaleUnchangedAsync(owner, organization, rice, oil);
    }

    [Fact]
    public async Task TheSameSaleIdWithADifferentProduct_Returns409_AndChangesNothing()
    {
        var (owner, organization, saleId, rice, oil) = await SaleOfFiveRiceAsync();

        var response = await SellAsync(owner, saleId, (oil, 5m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("sale.id_conflict", (await ReadAsync(response)).GetProperty("code").GetString());
        await AssertSaleUnchangedAsync(owner, organization, rice, oil);
    }

    [Fact]
    public async Task TheSameSaleIdWithAnAdditionalProduct_Returns409_AndChangesNothing()
    {
        var (owner, organization, saleId, rice, oil) = await SaleOfFiveRiceAsync();

        var response = await SellAsync(owner, saleId, (rice, 5m), (oil, 1m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("sale.id_conflict", (await ReadAsync(response)).GetProperty("code").GetString());
        await AssertSaleUnchangedAsync(owner, organization, rice, oil);
    }

    [Fact]
    public async Task TheSameSaleIdInAnotherOrganization_IsAnIndependentSale()
    {
        var (first, _, saleId, firstRice, _) = await SaleOfFiveRiceAsync();
        var (second, _) = await OwnerAsync();
        var rice = await ProductAsync(second, "Rice", stock: 10m);

        var response = await SellAsync(second, saleId, (rice, 3m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False((await ReadAsync(response)).GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(7m, await StockAsync(second, rice));
        Assert.Equal(15m, await StockAsync(first, firstRice));
    }

    [Fact]
    public async Task ASaleThatWasRefused_DoesNotReserveItsId()
    {
        var (owner, _) = await OwnerAsync();
        var rice = await ProductAsync(owner, "Rice", stock: 5m);
        var saleId = Guid.CreateVersion7();

        var refused = await SellAsync(owner, saleId, (rice, 9m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var retried = await SellAsync(owner, saleId, (rice, 5m));

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(0m, await StockAsync(owner, rice));
    }

    // ---- concurrency

    [Fact]
    public async Task ParallelSalesOfTheLastUnit_ProduceExactlyOneSale()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 1m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SellAsync(owner, null, (product, 1m))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.True(r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict));
        Assert.Equal(0m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<Sale>().CountAsync());
        Assert.Equal(1, await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.SaleCompleted));
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));
    }

    [Fact]
    public async Task ParallelSales_NeverSellMoreThanExists_AndStockMatchesTheSales()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 10m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SellAsync(owner, null, (product, 3m))));

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.InRange(succeeded, 1, 3);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.True(r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict));
        Assert.Equal(10m - (succeeded * 3m), await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(succeeded, await db.Set<Sale>().CountAsync());
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));
    }

    [Fact]
    public async Task ManyIdenticalSalesAtOnce_RecordExactlyOne()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 20m);
        var saleId = Guid.CreateVersion7();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SellAsync(owner, saleId, (product, 4m))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var other in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            // A loser either sees the stored sale (200) or, if its retries run out, a plain concurrency conflict; never an id conflict.
            Assert.True(other.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
            if (other.StatusCode == HttpStatusCode.Conflict)
            {
                Assert.NotEqual("sale.id_conflict", (await ReadAsync(other)).GetProperty("code").GetString());
            }
        }

        Assert.Equal(16m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<Sale>().CountAsync());
        Assert.Equal(1, await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.SaleCompleted));
    }

    // ---- voiding

    [Fact]
    public async Task AVoid_ReturnsTheStockExactlyOnce_KeepsTheSale_AndIsAudited()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, "Rice", stock: 10m);
        var oil = await ProductAsync(owner, "Oil", stock: 4m);
        var saleId = (await ReadAsync(await SellAsync(owner, null, (rice, 6m), (oil, 4m)))).GetProperty("saleId").GetGuid();
        Assert.Equal(4m, await StockAsync(owner, rice));
        Assert.Equal(0m, await StockAsync(owner, oil));

        var voided = await VoidAsync(owner, saleId, "  Wrong customer  ");

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var sale = await ReadAsync(voided);
        Assert.Equal("Voided", sale.GetProperty("status").GetString());
        Assert.Equal("Wrong customer", sale.GetProperty("voidReason").GetString());
        Assert.NotEqual(JsonValueKind.Null, sale.GetProperty("voidedAtUtc").ValueKind);
        Assert.Equal(2, sale.GetProperty("lines").GetArrayLength());
        Assert.Equal(10m, await StockAsync(owner, rice));
        Assert.Equal(4m, await StockAsync(owner, oil));

        var again = await VoidAsync(owner, saleId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("sale.not_voidable", (await ReadAsync(again)).GetProperty("code").GetString());
        Assert.Equal(10m, await StockAsync(owner, rice));

        await using var db = _database.CreateContext(organization);
        var returns = await db.Set<InventoryMovement>().Where(m => m.Reason == InventoryMovementReason.Return).ToListAsync();
        Assert.Equal(2, returns.Count);
        Assert.All(returns, m => Assert.Equal((InventorySourceType.Sale, saleId), (m.SourceType, m.SourceId)));
        var audit = await db.Set<AuditEvent>().SingleAsync(e => e.Action == "sales.sale_voided");
        Assert.Equal(saleId, audit.EntityId);
        Assert.DoesNotContain("Wrong customer", audit.Description);
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));

        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/sales/{saleId}")));
        Assert.Equal("Voided", read.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ManyVoidsAtOnce_ReturnTheStockExactlyOnce()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 10m);
        var saleId = (await ReadAsync(await SellAsync(owner, null, (product, 7m)))).GetProperty("saleId").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => VoidAsync(owner, saleId)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.True(r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict));
        Assert.Equal(10m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.Return));
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task AVoidWithoutAReason_Returns400_AndChangesNothing(string? reason)
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 10m);
        var saleId = (await ReadAsync(await SellAsync(owner, null, (product, 3m)))).GetProperty("saleId").GetGuid();

        var response = await VoidAsync(owner, saleId, reason);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("sale.void_reason_required", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("reason").GetArrayLength());
        Assert.Equal(7m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task VoidingAnUnknownSale_Returns404()
    {
        var (owner, _) = await OwnerAsync();

        var response = await VoidAsync(owner, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("sale.not_found", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheListShowsSalesNewestFirst_FilteredByStatus_WithValidatedPaging()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 20m);
        var first = (await ReadAsync(await SellAsync(owner, null, (product, 1m)))).GetProperty("saleId").GetGuid();
        var second = (await ReadAsync(await SellAsync(owner, null, (product, 2m)))).GetProperty("saleId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(owner, first)).StatusCode);

        var all = await ReadAsync(await owner.GetAsync(Relative("/api/v1/sales")));
        var voided = await ReadAsync(await owner.GetAsync(Relative("/api/v1/sales?status=voided")));
        var badStatus = await owner.GetAsync(Relative("/api/v1/sales?status=nonsense"));
        var badPage = await owner.GetAsync(Relative("/api/v1/sales?pageSize=500"));

        Assert.Equal([second, first], all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(2, all.GetProperty("totalCount").GetInt32());
        Assert.Equal([first], voided.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Equal("sale.status_invalid", (await ReadAsync(badStatus)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }

    // ---- authorization, cost visibility and tenant isolation

    public enum Operation
    {
        Create,
        List,
        Get,
        Void,
    }

    private static async Task<HttpResponseMessage> PerformAsync(HttpClient client, Operation operation, Guid product, Guid sale) => operation switch
    {
        Operation.Create => await SellAsync(client, null, (product, 1m)),
        Operation.List => await client.GetAsync(Relative("/api/v1/sales")),
        Operation.Get => await client.GetAsync(Relative($"/api/v1/sales/{sale}")),
        Operation.Void => await VoidAsync(client, sale),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    public static TheoryData<string, Operation, HttpStatusCode> Matrix()
    {
        var data = new TheoryData<string, Operation, HttpStatusCode>();
        foreach (var operation in Enum.GetValues<Operation>())
        {
            var success = operation == Operation.Create ? HttpStatusCode.Created : HttpStatusCode.OK;
            data.Add("Anonymous", operation, HttpStatusCode.Unauthorized);
            data.Add("Owner", operation, success);
            data.Add("Manager", operation, success);
            data.Add("Staff", operation, operation == Operation.Void ? HttpStatusCode.Forbidden : success);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryOperation_IsAllowedOrDenied_AsTheMatrixSays(string principal, Operation operation, HttpStatusCode expected)
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 20m);
        var sale = (await ReadAsync(await SellAsync(owner, null, (product, 2m)))).GetProperty("saleId").GetGuid();
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

        var response = await PerformAsync(client, operation, product, sale);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ADeniedVoid_ChangesNothing_AndWritesNoAuditEvent()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 10m);
        var sale = (await ReadAsync(await SellAsync(owner, null, (product, 4m)))).GetProperty("saleId").GetGuid();
        var staff = await SignInAsMemberAsync(_factory, _database, organization, Role.Staff);
        _clients.Add(staff);

        var denied = await VoidAsync(staff, sale);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(6m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(SaleStatus.Completed, (await db.Set<Sale>().SingleAsync()).Status);
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.sale_voided"));
    }

    [Fact]
    public async Task AStaffCashier_CanSell_ButNeverSeesCostPrices()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, price: 10m, cost: 6m, stock: 10m);
        var staff = await SignInAsMemberAsync(_factory, _database, organization, Role.Staff);
        _clients.Add(staff);

        var response = await SellAsync(staff, null, (product, 2m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = (await ReadAsync(response)).GetProperty("sale");
        Assert.Equal(20m, sale.GetProperty("total").GetDecimal());
        Assert.Equal(JsonValueKind.Null, sale.GetProperty("totalCost").ValueKind);
        var line = LineOf(sale, product);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("costPrice").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("lineCost").ValueKind);
        var read = await ReadAsync(await staff.GetAsync(Relative($"/api/v1/sales/{sale.GetProperty("id").GetGuid()}")));
        Assert.Equal(JsonValueKind.Null, read.GetProperty("totalCost").ValueKind);
        var asOwner = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/sales/{sale.GetProperty("id").GetGuid()}")));
        Assert.Equal(12m, asOwner.GetProperty("totalCost").GetDecimal());
        Assert.Equal(sale.GetProperty("id").GetGuid(), (await ReadAsync(await owner.GetAsync(Relative("/api/v1/sales")))).GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AnotherOrganizationsSaleAndProduct_AreNotFound_AndNeverChanged()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 10m);
        var sale = (await ReadAsync(await SellAsync(owner, null, (product, 3m)))).GetProperty("saleId").GetGuid();
        var (other, _) = await OwnerAsync();
        var otherProduct = await ProductAsync(other, stock: 10m);

        var get = await other.GetAsync(Relative($"/api/v1/sales/{sale}"));
        var voidIt = await VoidAsync(other, sale);
        var sellMine = await SellAsync(other, null, (product, 1m));

        Assert.All([get, voidIt, sellMine], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal("sale.not_found", (await ReadAsync(get)).GetProperty("code").GetString());
        Assert.Equal("product.not_found", (await ReadAsync(sellMine)).GetProperty("code").GetString());
        Assert.Equal(7m, await StockAsync(owner, product));
        Assert.Equal(10m, await StockAsync(other, otherProduct));
        var mine = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/sales/{sale}")));
        Assert.Equal("Completed", mine.GetProperty("status").GetString());
        var theirs = await ReadAsync(await other.GetAsync(Relative("/api/v1/sales")));
        Assert.Equal(0, theirs.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task APostWithoutAnAntiforgeryToken_IsRejected()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 5m);

        var response = await PostAsync(owner, "/api/v1/sales", new { lines = new[] { new { productId = product, quantity = 1 } } }, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery.invalid", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(5m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task ThereIsNoWayToEditOrDeleteASale()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 5m);
        var sale = (await ReadAsync(await SellAsync(owner, null, (product, 2m)))).GetProperty("saleId").GetGuid();

        var attempts = new[]
        {
            await SendAsync(owner, HttpMethod.Put, $"/api/v1/sales/{sale}", new { lines = Array.Empty<object>() }),
            await SendAsync(owner, HttpMethod.Delete, $"/api/v1/sales/{sale}", body: null),
            await SendAsync(owner, HttpMethod.Put, $"/api/v1/sales/{sale}/void", new { reason = "x" }),
        };

        Assert.All(attempts, response => Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed));
        Assert.Equal(3m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheSalesEndpoints()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        var create = paths.GetProperty("/api/v1/sales").GetProperty("post");
        Assert.Equal("CreateSale", create.GetProperty("operationId").GetString());
        Assert.True(create.GetProperty("responses").TryGetProperty("422", out _));
        Assert.True(create.GetProperty("responses").TryGetProperty("409", out _));
        Assert.Equal("ListSales", paths.GetProperty("/api/v1/sales").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("GetSale", paths.GetProperty("/api/v1/sales/{id}").GetProperty("get").GetProperty("operationId").GetString());
        var voidOperation = paths.GetProperty("/api/v1/sales/{id}/void").GetProperty("post");
        Assert.Equal("VoidSale", voidOperation.GetProperty("operationId").GetString());
        Assert.True(voidOperation.GetProperty("responses").TryGetProperty("422", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("SaleDto", schemas);
        Assert.DoesNotContain("Sale", schemas);
        Assert.DoesNotContain("SaleLine", schemas);
    }
}
