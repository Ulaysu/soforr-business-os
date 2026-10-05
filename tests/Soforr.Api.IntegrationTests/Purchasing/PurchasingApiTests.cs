using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Audit;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Purchasing;

/// <summary>
/// The Purchasing endpoints through the running application against real PostgreSQL: creating suppliers and orders,
/// the order's lifecycle, receiving goods (stock, partial receipts, idempotency, atomicity, concurrency), the
/// authorization matrix, tenant isolation and the audit trail.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PurchasingApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public PurchasingApiTests(PostgresDatabaseFixture database)
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

    private static async Task<Guid> CreateProductAsync(HttpClient owner, string name = "Rice", string sku = "")
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = sku.Length > 0 ? sku : "SKU-" + Guid.NewGuid().ToString("N"), name, unitOfMeasure = "bag", sellingPrice = 10, costPrice = 5 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateSupplierAsync(HttpClient owner, string name = "Gambia Grain Ltd")
    {
        var response = await PostAsync(owner, "/api/v1/purchasing/suppliers", new { name, contactDetails = "+220 555 0100" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> CreateOrderAsync(HttpClient client, Guid? supplier, params (Guid? Product, decimal? Quantity, decimal? UnitCost)[] lines) =>
        PostAsync(client, "/api/v1/purchasing/orders", new { supplierId = supplier, reference = "Q-1", lines = lines.Select(l => new { productId = l.Product, quantity = l.Quantity, unitCost = l.UnitCost }).ToArray() });

    private static async Task<JsonElement> OrderAsync(HttpClient client, Guid supplier, params (Guid Product, decimal Quantity, decimal UnitCost)[] lines)
    {
        var response = await CreateOrderAsync(client, supplier, lines.Select(l => ((Guid?)l.Product, (decimal?)l.Quantity, (decimal?)l.UnitCost)).ToArray());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> PlacedOrderAsync(HttpClient client, Guid supplier, params (Guid Product, decimal Quantity, decimal UnitCost)[] lines)
    {
        var order = await OrderAsync(client, supplier, lines);
        var placed = await PostAsync(client, $"/api/v1/purchasing/orders/{order.GetProperty("id").GetGuid()}/place", null);
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        return await ReadAsync(placed);
    }

    private static Task<HttpResponseMessage> ReceiveAsync(HttpClient client, Guid order, Guid? receiptId, params (Guid Line, decimal Quantity)[] lines) =>
        PostAsync(client, $"/api/v1/purchasing/orders/{order}/receipts", new { receiptId, lines = lines.Select(l => new { lineId = l.Line, quantity = l.Quantity }).ToArray() });

    /// <summary>The order's line for a product. Lines come back in a stable order (name, SKU, id), not in the order they were entered, so tests find them by product.</summary>
    private static Guid LineOf(JsonElement order, Guid product) =>
        order.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("productId").GetGuid() == product).GetProperty("id").GetGuid();

    private static JsonElement LineElement(JsonElement order, Guid product) =>
        order.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("productId").GetGuid() == product);

    private static Guid LineId(JsonElement order, int index) => order.GetProperty("lines")[index].GetProperty("id").GetGuid();

    private static async Task<decimal> StockAsync(HttpClient client, Guid product) =>
        (await ReadAsync(await client.GetAsync(Relative($"/api/v1/inventory/stock/{product}")))).GetProperty("quantity").GetDecimal();

    // ---- suppliers

    [Fact]
    public async Task ASupplier_IsCreatedListedAndAudited()
    {
        var (owner, organization) = await OwnerAsync();

        var id = await CreateSupplierAsync(owner, "  Banjul Foods  ");

        var list = await ReadAsync(await owner.GetAsync(Relative("/api/v1/purchasing/suppliers")));
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("Banjul Foods", item.GetProperty("name").GetString());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "purchasing.supplier_created" && e.EntityId == id));
    }

    [Theory]
    [InlineData(null, "name", "supplier.name_required")]
    [InlineData("   ", "name", "supplier.name_required")]
    public async Task AnInvalidSupplier_Returns400_WithTheField(string? name, string field, string code)
    {
        var (owner, _) = await OwnerAsync();

        var response = await PostAsync(owner, "/api/v1/purchasing/suppliers", new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty(field).GetArrayLength());
    }

    // ---- creating an order

    [Fact]
    public async Task AnOrder_IsCreatedAsADraft_InTheOrganizationsCurrency_WithComputedTotalsAndProductSnapshots()
    {
        var (owner, organization) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var rice = await CreateProductAsync(owner, "Rice", "RICE-50");
        var oil = await CreateProductAsync(owner, "Oil", "OIL-5L");

        var response = await CreateOrderAsync(owner, supplier, (rice, 10m, 12.5m), (oil, 3m, 99.99m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadAsync(response);
        Assert.Equal($"/api/v1/purchasing/orders/{order.GetProperty("id").GetGuid()}", response.Headers.Location?.OriginalString);
        Assert.Equal("Draft", order.GetProperty("status").GetString());
        Assert.Equal("GMD", order.GetProperty("currency").GetString());
        Assert.Equal(424.97m, order.GetProperty("total").GetDecimal());
        Assert.Equal(["Oil", "Rice"], order.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("name").GetString()!).ToArray());
        var first = LineElement(order, rice);
        Assert.Equal("RICE-50", first.GetProperty("sku").GetString());
        Assert.Equal("Rice", first.GetProperty("name").GetString());
        Assert.Equal("bag", first.GetProperty("unitOfMeasure").GetString());
        Assert.Equal(125m, first.GetProperty("lineTotal").GetDecimal());
        Assert.Equal(0m, first.GetProperty("receivedQuantity").GetDecimal());

        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/purchasing/orders/{order.GetProperty("id").GetGuid()}")));
        Assert.Equal(424.97m, read.GetProperty("total").GetDecimal());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "purchasing.order_created"));
    }

    [Fact]
    public async Task ACurrencyInTheRequestBody_IsNotHonored()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);

        var response = await PostAsync(owner, "/api/v1/purchasing/orders", new { supplierId = supplier, currency = "USD", total = 1, lines = new[] { new { productId = product, quantity = 2, unitCost = 3, currency = "EUR" } } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadAsync(response);
        Assert.Equal("GMD", order.GetProperty("currency").GetString());
        Assert.Equal(6m, order.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task ChangingAProductAfterOrdering_DoesNotChangeTheLine()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner, "Rice", "RICE-1");
        var order = await OrderAsync(owner, supplier, (product, 1m, 4m));

        var update = await SendAsync(owner, HttpMethod.Put, $"/api/v1/products/{product}", new { sku = "RICE-2", name = "Brown rice", unitOfMeasure = "kg", sellingPrice = 20, costPrice = 9 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/purchasing/orders/{order.GetProperty("id").GetGuid()}")));
        var line = read.GetProperty("lines")[0];
        Assert.Equal("RICE-1", line.GetProperty("sku").GetString());
        Assert.Equal("Rice", line.GetProperty("name").GetString());
        Assert.Equal("bag", line.GetProperty("unitOfMeasure").GetString());
        Assert.Equal(4m, line.GetProperty("unitCost").GetDecimal());
    }

    public static TheoryData<string, string?> InvalidOrders() => new()
    {
        { "no supplier", "purchase_order.supplier_required" },
        { "no lines", "purchase_order.no_lines" },
        { "zero quantity", "purchase_order.line_quantity_required" },
        { "negative quantity", "quantity.negative" },
        { "negative cost", "purchase_order.line_unit_cost_negative" },
        { "too many decimals", "quantity.scale_exceeded" },
        { "duplicate product", "purchase_order.line_product_duplicate" },
        { "missing cost", "purchase_order.line_unit_cost_required" },
    };

    [Theory]
    [MemberData(nameof(InvalidOrders))]
    public async Task AnInvalidOrder_Returns400_WithAStableCode_AndCreatesNothing(string scenario, string? code)
    {
        var (owner, organization) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);

        var response = scenario switch
        {
            "no supplier" => await CreateOrderAsync(owner, null, (product, 1m, 1m)),
            "no lines" => await CreateOrderAsync(owner, supplier),
            "zero quantity" => await CreateOrderAsync(owner, supplier, (product, 0m, 1m)),
            "negative quantity" => await CreateOrderAsync(owner, supplier, (product, -1m, 1m)),
            "negative cost" => await CreateOrderAsync(owner, supplier, (product, 1m, -1m)),
            "too many decimals" => await CreateOrderAsync(owner, supplier, (product, 1.00001m, 1m)),
            "duplicate product" => await CreateOrderAsync(owner, supplier, (product, 1m, 1m), (product, 2m, 1m)),
            "missing cost" => await CreateOrderAsync(owner, supplier, (product, 1m, null)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadAsync(response)).GetProperty("code").GetString());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<Soforr.Domain.Purchasing.PurchaseOrder>().CountAsync());
    }

    [Fact]
    public async Task AnOrderForAnUnknownSupplierOrProduct_Returns404()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);

        var noSupplier = await CreateOrderAsync(owner, Guid.CreateVersion7(), (product, 1m, 1m));
        var noProduct = await CreateOrderAsync(owner, supplier, (Guid.CreateVersion7(), 1m, 1m));

        Assert.Equal(HttpStatusCode.NotFound, noSupplier.StatusCode);
        Assert.Equal("supplier.not_found", (await ReadAsync(noSupplier)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, noProduct.StatusCode);
        Assert.Equal("product.not_found", (await ReadAsync(noProduct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnInactiveProduct_CannotBeOrdered()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        Assert.True((await PostAsync(owner, $"/api/v1/products/{product}/deactivate", null)).IsSuccessStatusCode);

        var response = await CreateOrderAsync(owner, supplier, (product, 1m, 1m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("purchase_order.product_inactive", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // ---- lifecycle

    [Fact]
    public async Task AnOrder_IsPlacedOnce_AndCanThenBeCancelledOnlyIfNothingWasReceived()
    {
        var (owner, organization) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await OrderAsync(owner, supplier, (product, 5m, 2m));
        var id = order.GetProperty("id").GetGuid();

        var placed = await PostAsync(owner, $"/api/v1/purchasing/orders/{id}/place", null);
        var placedAgain = await PostAsync(owner, $"/api/v1/purchasing/orders/{id}/place", null);

        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        Assert.Equal("Placed", (await ReadAsync(placed)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, placedAgain.StatusCode);
        Assert.Equal("purchase_order.cannot_place", (await ReadAsync(placedAgain)).GetProperty("code").GetString());

        var cancelled = await PostAsync(owner, $"/api/v1/purchasing/orders/{id}/cancel", null);
        Assert.Equal("Cancelled", (await ReadAsync(cancelled)).GetProperty("status").GetString());
        var receiveCancelled = await ReceiveAsync(owner, id, null, (LineId(order, 0), 1m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, receiveCancelled.StatusCode);
        Assert.Equal("purchase_order.not_receivable", (await ReadAsync(receiveCancelled)).GetProperty("code").GetString());
        Assert.Equal(0m, await StockAsync(owner, product));

        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "purchasing.order_placed"));
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "purchasing.order_cancelled"));
    }

    [Fact]
    public async Task AnOrderThatReceivedGoods_CannotBeCancelled()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await PlacedOrderAsync(owner, supplier, (product, 10m, 2m));
        var id = order.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await ReceiveAsync(owner, id, null, (LineId(order, 0), 4m))).StatusCode);

        var cancel = await PostAsync(owner, $"/api/v1/purchasing/orders/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, cancel.StatusCode);
        Assert.Equal("purchase_order.cannot_cancel", (await ReadAsync(cancel)).GetProperty("code").GetString());
        Assert.Equal(4m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task ADraft_CannotBeReceived()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await OrderAsync(owner, supplier, (product, 10m, 2m));

        var response = await ReceiveAsync(owner, order.GetProperty("id").GetGuid(), null, (LineId(order, 0), 1m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("purchase_order.not_receivable", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task TheListShowsOrdersNewestFirst_FilteredByStatusAndSupplier()
    {
        var (owner, _) = await OwnerAsync();
        var first = await CreateSupplierAsync(owner, "First");
        var second = await CreateSupplierAsync(owner, "Second");
        var product = await CreateProductAsync(owner);
        var draft = await OrderAsync(owner, first, (product, 1m, 1m));
        var placed = await PlacedOrderAsync(owner, second, (product, 1m, 1m));

        var all = await ReadAsync(await owner.GetAsync(Relative("/api/v1/purchasing/orders")));
        var onlyPlaced = await ReadAsync(await owner.GetAsync(Relative("/api/v1/purchasing/orders?status=placed")));
        var bySupplier = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/purchasing/orders?supplierId={first}")));
        var badStatus = await owner.GetAsync(Relative("/api/v1/purchasing/orders?status=nonsense"));
        var badPage = await owner.GetAsync(Relative("/api/v1/purchasing/orders?pageSize=500"));

        Assert.Equal([placed.GetProperty("id").GetGuid(), draft.GetProperty("id").GetGuid()], all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(1, all.GetProperty("items")[0].GetProperty("lineCount").GetInt32());
        Assert.Equal([placed.GetProperty("id").GetGuid()], onlyPlaced.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal([draft.GetProperty("id").GetGuid()], bySupplier.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Equal("purchase_order.status_invalid", (await ReadAsync(badStatus)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }

    // ---- receiving

    [Fact]
    public async Task Receiving_AddsStockOnce_RecordsMovementsAndAnAuditEvent_AndSupportsPartialReceipts()
    {
        var (owner, organization) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var rice = await CreateProductAsync(owner, "Rice");
        var oil = await CreateProductAsync(owner, "Oil");
        var order = await PlacedOrderAsync(owner, supplier, (rice, 10m, 2m), (oil, 4m, 5m));
        var id = order.GetProperty("id").GetGuid();

        var firstReceiptId = Guid.CreateVersion7();
        var first = await ReceiveAsync(owner, id, firstReceiptId, (LineOf(order, rice), 6m));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await ReadAsync(first);
        Assert.Equal(firstReceiptId, firstBody.GetProperty("receiptId").GetGuid());
        Assert.False(firstBody.GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal("PartiallyReceived", firstBody.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(6m, LineElement(firstBody.GetProperty("order"), rice).GetProperty("receivedQuantity").GetDecimal());
        Assert.Equal(4m, LineElement(firstBody.GetProperty("order"), rice).GetProperty("outstandingQuantity").GetDecimal());
        Assert.Equal(6m, await StockAsync(owner, rice));
        Assert.Equal(0m, await StockAsync(owner, oil));

        var second = await ReceiveAsync(owner, id, null, (LineOf(order, rice), 4m), (LineOf(order, oil), 4m));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondOrder = (await ReadAsync(second)).GetProperty("order");
        Assert.Equal("Received", secondOrder.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, secondOrder.GetProperty("closedAtUtc").ValueKind);
        Assert.Equal(10m, await StockAsync(owner, rice));
        Assert.Equal(4m, await StockAsync(owner, oil));

        var history = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{rice}/movements")));
        Assert.Equal(2, history.GetProperty("items").GetArrayLength());
        Assert.All(history.GetProperty("items").EnumerateArray(), movement =>
        {
            Assert.Equal("PurchaseReceived", movement.GetProperty("reason").GetString());
            Assert.Equal("PurchaseReceipt", movement.GetProperty("sourceType").GetString());
        });

        await using var db = _database.CreateContext(organization);
        Assert.Equal(2, await db.Set<AuditEvent>().CountAsync(e => e.Action == "purchasing.goods_received" && e.EntityId == id));
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));

        var tooMuch = await ReceiveAsync(owner, id, null, (LineOf(order, rice), 1m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.StatusCode);
        Assert.Equal("purchase_order.not_receivable", (await ReadAsync(tooMuch)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task RetryingAReceiptWithTheSameId_Returns200_AndChangesNothing()
    {
        var (owner, organization) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await PlacedOrderAsync(owner, supplier, (product, 10m, 2m));
        var id = order.GetProperty("id").GetGuid();
        var receiptId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await ReceiveAsync(owner, id, receiptId, (LineId(order, 0), 3m))).StatusCode);

        var again = await ReceiveAsync(owner, id, receiptId, (LineId(order, 0), 3m));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await ReadAsync(again);
        Assert.True(body.GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(3m, body.GetProperty("order").GetProperty("lines")[0].GetProperty("receivedQuantity").GetDecimal());
        Assert.Equal(3m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<InventoryMovement>().CountAsync());
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "purchasing.goods_received"));
    }

    [Fact]
    public async Task AReceiptIdAlreadyUsedOnAnotherOrdersProducts_IsAConflict_AndChangesNothing()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var first = await CreateProductAsync(owner, "First");
        var second = await CreateProductAsync(owner, "Second");
        var orderA = await PlacedOrderAsync(owner, supplier, (first, 5m, 1m));
        var orderB = await PlacedOrderAsync(owner, supplier, (second, 5m, 1m));
        var receiptId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await ReceiveAsync(owner, orderA.GetProperty("id").GetGuid(), receiptId, (LineId(orderA, 0), 2m))).StatusCode);

        var response = await ReceiveAsync(owner, orderB.GetProperty("id").GetGuid(), receiptId, (LineId(orderB, 0), 2m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("purchase_order.receipt_id_in_use", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0m, await StockAsync(owner, second));
    }

    [Fact]
    public async Task ManyIdenticalReceiptsAtOnce_RecordExactlyOne()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await PlacedOrderAsync(owner, supplier, (product, 100m, 2m));
        var id = order.GetProperty("id").GetGuid();
        var receiptId = Guid.CreateVersion7();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ReceiveAsync(owner, id, receiptId, (LineId(order, 0), 7m))));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflicts = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, created);
        Assert.Equal(6, created + ok + conflicts);
        Assert.Equal(7m, await StockAsync(owner, product));
        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/purchasing/orders/{id}")));
        Assert.Equal(7m, read.GetProperty("lines")[0].GetProperty("receivedQuantity").GetDecimal());
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));
    }

    [Fact]
    public async Task DifferentReceiptsAtOnce_NeverReceiveMoreThanWasOrdered_AndStockMatchesTheOrder()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await PlacedOrderAsync(owner, supplier, (product, 10m, 2m));
        var id = order.GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ReceiveAsync(owner, id, Guid.CreateVersion7(), (LineId(order, 0), 3m))));

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.InRange(succeeded, 1, 3);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.True(r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity));
        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/purchasing/orders/{id}")));
        var received = read.GetProperty("lines")[0].GetProperty("receivedQuantity").GetDecimal();
        Assert.Equal(succeeded * 3m, received);
        Assert.InRange(received, 0m, 10m);
        Assert.Equal(received, await StockAsync(owner, product));
        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(_database.ConnectionString));
    }

    [Fact]
    public async Task AReceiptThatExceedsWhatIsOutstanding_Returns422_AndChangesNothing()
    {
        var (owner, organization) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var rice = await CreateProductAsync(owner, "Rice");
        var oil = await CreateProductAsync(owner, "Oil");
        var order = await PlacedOrderAsync(owner, supplier, (rice, 10m, 2m), (oil, 2m, 5m));
        var id = order.GetProperty("id").GetGuid();

        // The first line is fine, the second exceeds what was ordered: the whole receipt is refused.
        var response = await ReceiveAsync(owner, id, null, (LineOf(order, rice), 5m), (LineOf(order, oil), 3m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("purchase_order.receipt_exceeds_ordered", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0m, await StockAsync(owner, rice));
        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/purchasing/orders/{id}")));
        Assert.Equal("Placed", read.GetProperty("status").GetString());
        Assert.Equal(0m, LineElement(read, rice).GetProperty("receivedQuantity").GetDecimal());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<InventoryMovement>().CountAsync());
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == "purchasing.goods_received"));
    }

    [Theory]
    [InlineData("empty", "purchase_order.receipt_empty")]
    [InlineData("zero", "purchase_order.receipt_quantity_required")]
    [InlineData("negative", "quantity.negative")]
    [InlineData("duplicate", "purchase_order.receipt_line_duplicate")]
    [InlineData("no quantity", "purchase_order.receipt_quantity_required")]
    public async Task AnInvalidReceipt_Returns400_AndChangesNothing(string scenario, string code)
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await PlacedOrderAsync(owner, supplier, (product, 10m, 2m));
        var id = order.GetProperty("id").GetGuid();
        var line = LineId(order, 0);

        var response = scenario switch
        {
            "empty" => await ReceiveAsync(owner, id, null),
            "zero" => await ReceiveAsync(owner, id, null, (line, 0m)),
            "negative" => await ReceiveAsync(owner, id, null, (line, -1m)),
            "duplicate" => await ReceiveAsync(owner, id, null, (line, 1m), (line, 1m)),
            "no quantity" => await PostAsync(owner, $"/api/v1/purchasing/orders/{id}/receipts", new { lines = new[] { new { lineId = line } } }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task ALineThatIsNotOnTheOrder_Returns404()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await PlacedOrderAsync(owner, supplier, (product, 10m, 2m));

        var response = await ReceiveAsync(owner, order.GetProperty("id").GetGuid(), null, (Guid.CreateVersion7(), 1m));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("purchase_order.line_not_found", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // ---- authorization and tenant isolation

    public enum Operation
    {
        CreateSupplier,
        ListSuppliers,
        CreateOrder,
        ListOrders,
        GetOrder,
        Place,
        Cancel,
        Receive,
    }

    private static async Task<HttpResponseMessage> PerformAsync(HttpClient client, Operation operation, Guid supplier, Guid product, JsonElement placed, JsonElement draft) => operation switch
    {
        Operation.CreateSupplier => await PostAsync(client, "/api/v1/purchasing/suppliers", new { name = "Another supplier" }),
        Operation.ListSuppliers => await client.GetAsync(Relative("/api/v1/purchasing/suppliers")),
        Operation.CreateOrder => await CreateOrderAsync(client, supplier, (product, 1m, 1m)),
        Operation.ListOrders => await client.GetAsync(Relative("/api/v1/purchasing/orders")),
        Operation.GetOrder => await client.GetAsync(Relative($"/api/v1/purchasing/orders/{placed.GetProperty("id").GetGuid()}")),
        Operation.Place => await PostAsync(client, $"/api/v1/purchasing/orders/{draft.GetProperty("id").GetGuid()}/place", null),
        Operation.Cancel => await PostAsync(client, $"/api/v1/purchasing/orders/{draft.GetProperty("id").GetGuid()}/cancel", null),
        Operation.Receive => await ReceiveAsync(client, placed.GetProperty("id").GetGuid(), null, (LineId(placed, 0), 1m)),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    public static TheoryData<string, Operation, HttpStatusCode> Matrix()
    {
        var data = new TheoryData<string, Operation, HttpStatusCode>();
        foreach (var operation in Enum.GetValues<Operation>())
        {
            var success = operation switch
            {
                Operation.CreateSupplier or Operation.CreateOrder or Operation.Receive => HttpStatusCode.Created,
                _ => HttpStatusCode.OK,
            };
            data.Add("Anonymous", operation, HttpStatusCode.Unauthorized);
            data.Add("Owner", operation, success);
            data.Add("Manager", operation, success);
            data.Add("Staff", operation, HttpStatusCode.Forbidden);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryOperation_IsAllowedOrDenied_AsTheMatrixSays(string principal, Operation operation, HttpStatusCode expected)
    {
        var (owner, organization) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var placed = await PlacedOrderAsync(owner, supplier, (product, 5m, 1m));
        var draft = await OrderAsync(owner, supplier, (product, 5m, 1m));
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

        var response = await PerformAsync(client, operation, supplier, product, placed, draft);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ADeniedReceipt_ChangesNothing()
    {
        var (owner, organization) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await PlacedOrderAsync(owner, supplier, (product, 5m, 1m));
        var staff = await SignInAsMemberAsync(_factory, _database, organization, Role.Staff);
        _clients.Add(staff);

        var denied = await ReceiveAsync(staff, order.GetProperty("id").GetGuid(), null, (LineId(order, 0), 5m));

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<InventoryMovement>().CountAsync());
    }

    [Fact]
    public async Task AnotherOrganizationsSupplierOrderAndProduct_AreNotFound_AndNeverChanged()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await PlacedOrderAsync(owner, supplier, (product, 5m, 1m));
        var id = order.GetProperty("id").GetGuid();
        var (other, _) = await OwnerAsync();
        var otherSupplier = await CreateSupplierAsync(other, "Theirs");
        var otherProduct = await CreateProductAsync(other, "Theirs");

        var get = await other.GetAsync(Relative($"/api/v1/purchasing/orders/{id}"));
        var place = await PostAsync(other, $"/api/v1/purchasing/orders/{id}/place", null);
        var cancel = await PostAsync(other, $"/api/v1/purchasing/orders/{id}/cancel", null);
        var receive = await ReceiveAsync(other, id, null, (LineId(order, 0), 5m));
        var theirsWithMyProduct = await CreateOrderAsync(other, otherSupplier, (product, 1m, 1m));
        var theirsWithMySupplier = await CreateOrderAsync(other, supplier, (otherProduct, 1m, 1m));

        Assert.All([get, place, cancel, receive, theirsWithMyProduct, theirsWithMySupplier], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal("purchase_order.not_found", (await ReadAsync(get)).GetProperty("code").GetString());
        Assert.Equal(0m, await StockAsync(owner, product));
        var mine = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/purchasing/orders/{id}")));
        Assert.Equal("Placed", mine.GetProperty("status").GetString());
        var theirs = await ReadAsync(await other.GetAsync(Relative("/api/v1/purchasing/orders")));
        Assert.Equal(0, theirs.GetProperty("totalCount").GetInt32());
        var theirSuppliers = await ReadAsync(await other.GetAsync(Relative("/api/v1/purchasing/suppliers")));
        Assert.Equal(["Theirs"], theirSuppliers.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task APostWithoutAnAntiforgeryToken_IsRejected()
    {
        var (owner, _) = await OwnerAsync();

        var response = await PostAsync(owner, "/api/v1/purchasing/suppliers", new { name = "No token" }, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery.invalid", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ThereIsNoWayToSetAReceivedQuantity_OrEditAPlacedOrder()
    {
        var (owner, _) = await OwnerAsync();
        var supplier = await CreateSupplierAsync(owner);
        var product = await CreateProductAsync(owner);
        var order = await PlacedOrderAsync(owner, supplier, (product, 5m, 1m));
        var id = order.GetProperty("id").GetGuid();

        var attempts = new[]
        {
            await SendAsync(owner, HttpMethod.Put, $"/api/v1/purchasing/orders/{id}", new { reference = "x" }),
            await SendAsync(owner, HttpMethod.Delete, $"/api/v1/purchasing/orders/{id}", body: null),
            await SendAsync(owner, HttpMethod.Put, $"/api/v1/purchasing/orders/{id}/receipts", new { lines = Array.Empty<object>() }),
        };

        Assert.All(attempts, response => Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed));
        Assert.Equal(0m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsThePurchasingEndpoints()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("CreateSupplier", paths.GetProperty("/api/v1/purchasing/suppliers").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("CreatePurchaseOrder", paths.GetProperty("/api/v1/purchasing/orders").GetProperty("post").GetProperty("operationId").GetString());
        var receive = paths.GetProperty("/api/v1/purchasing/orders/{id}/receipts").GetProperty("post");
        Assert.Equal("ReceivePurchase", receive.GetProperty("operationId").GetString());
        Assert.True(receive.GetProperty("responses").TryGetProperty("422", out _));
        Assert.True(receive.GetProperty("responses").TryGetProperty("409", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("PurchaseOrderDto", schemas);
        Assert.DoesNotContain("PurchaseOrder", schemas);
        Assert.DoesNotContain("Supplier", schemas);
    }
}
