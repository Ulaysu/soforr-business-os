using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Domain.Audit;
using Soforr.Domain.Identity;
using Soforr.Domain.Sales;
using static Soforr.Api.IntegrationTests.Identity.AuthClient;

namespace Soforr.Api.IntegrationTests.Sales;

/// <summary>
/// Controlled price overrides and discounts (SALES-004) through the running application against real PostgreSQL: who may state a
/// price, the cost floor, validation, what is recorded (catalog price, price charged, reason, actor), the audit event, the
/// totals, retries with prices, stock and tenant isolation.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PriceOverrideApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public PriceOverrideApiTests(PostgresDatabaseFixture database)
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

    private async Task<HttpClient> MemberAsync(Guid organization, Role role)
    {
        var client = await SignInAsMemberAsync(_factory, _database, organization, role);
        _clients.Add(client);
        return client;
    }

    private static async Task<Guid> ProductAsync(HttpClient owner, decimal price = 10m, decimal cost = 6m, decimal stock = 100m)
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = "SKU-" + Guid.NewGuid().ToString("N"), name = "Rice", unitOfMeasure = "bag", sellingPrice = price, costPrice = cost });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(owner, "/api/v1/inventory/adjustments", new { productId = id, quantityChange = stock, note = "Opening stock" })).StatusCode);
        return id;
    }

    private static Task<HttpResponseMessage> SellAsync(HttpClient client, Guid product, decimal quantity, decimal? unitPrice = null, string? reason = null, Guid? saleId = null) =>
        PostAsync(client, "/api/v1/sales", new { saleId, lines = new[] { new { productId = product, quantity, unitPrice, priceReason = reason } } });

    private static async Task<decimal> StockAsync(HttpClient client, Guid product) =>
        (await ReadAsync(await client.GetAsync(Relative($"/api/v1/inventory/stock/{product}")))).GetProperty("quantity").GetDecimal();

    // ---- what is recorded

    [Fact]
    public async Task ADiscount_RecordsTheCatalogPrice_ThePriceCharged_TheReasonAndTheActor_AndTotalsAreServerComputed()
    {
        var (owner, organization) = await OwnerAsync();
        var manager = await MemberAsync(organization, Role.Manager);
        var rice = await ProductAsync(owner, price: 10m, cost: 6m);

        var response = await SellAsync(manager, rice, 4m, unitPrice: 8.5m, reason: "  Loyal customer  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = (await ReadAsync(response)).GetProperty("sale");
        var line = sale.GetProperty("lines")[0];
        Assert.Equal((10m, 8.5m, 34m, "Loyal customer"), (line.GetProperty("catalogUnitPrice").GetDecimal(), line.GetProperty("unitPrice").GetDecimal(), line.GetProperty("lineTotal").GetDecimal(), line.GetProperty("priceOverrideReason").GetString()));
        Assert.Equal((34m, 6m, 24m), (sale.GetProperty("total").GetDecimal(), sale.GetProperty("totalDiscount").GetDecimal(), sale.GetProperty("totalCost").GetDecimal()));
        Assert.Equal(96m, await StockAsync(owner, rice));

        await using var db = _database.CreateContext(organization);
        var stored = await db.Set<Sale>().Include(s => s.Lines).SingleAsync(s => s.Id == sale.GetProperty("id").GetGuid());
        Assert.Equal(sale.GetProperty("createdByUserId").GetGuid(), stored.CreatedByUserId);
        Assert.True(stored.HasPriceOverride);
    }

    [Fact]
    public async Task APriceHigherThanTheCatalog_IsAnOverrideToo_AndNegativeDiscountIsShownAsSuch()
    {
        var (owner, _) = await OwnerAsync();
        var rice = await ProductAsync(owner, price: 10m);

        var sale = (await ReadAsync(await SellAsync(owner, rice, 2m, unitPrice: 12m, reason: "Rush order"))).GetProperty("sale");

        Assert.Equal((24m, -4m), (sale.GetProperty("total").GetDecimal(), sale.GetProperty("totalDiscount").GetDecimal()));
    }

    [Fact]
    public async Task AFreeLine_IsAllowedForTheOwner_ButAPriceBelowZeroNever()
    {
        var (owner, _) = await OwnerAsync();
        var rice = await ProductAsync(owner);

        var free = await SellAsync(owner, rice, 1m, unitPrice: 0m, reason: "Sample");
        var negative = await SellAsync(owner, rice, 1m, unitPrice: -1m, reason: "Oops");

        Assert.Equal(HttpStatusCode.Created, free.StatusCode);
        Assert.Equal(0m, (await ReadAsync(free)).GetProperty("sale").GetProperty("total").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        var problem = await ReadAsync(negative);
        Assert.Equal("sale.line_price_negative", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("lines[0].unitPrice").GetArrayLength());
    }

    [Fact]
    public async Task ThePriceMatchingTheCatalog_NeedsNoReason_AndIsNotAnOverride()
    {
        var (owner, _) = await OwnerAsync();
        var rice = await ProductAsync(owner, price: 10m);

        var response = await SellAsync(owner, rice, 1m, unitPrice: 10m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = (await ReadAsync(response)).GetProperty("sale");
        Assert.Equal(0m, sale.GetProperty("totalDiscount").GetDecimal());
    }

    // ---- validation

    [Theory]
    [InlineData(8, null, "sale.line_override_reason_required")]
    [InlineData(8, "   ", "sale.line_override_reason_required")]
    [InlineData(10, "Not needed", "sale.line_override_reason_unexpected")]
    public async Task APriceNeedsAReason_ExactlyWhenItDiffersFromTheCatalog(double price, string? reason, string code)
    {
        var (owner, _) = await OwnerAsync();
        var rice = await ProductAsync(owner, price: 10m);

        var response = await SellAsync(owner, rice, 1m, (decimal)price, reason);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(100m, await StockAsync(owner, rice));
    }

    [Fact]
    public async Task AReasonWithoutAPrice_AndATooLongReason_AndTooManyDecimals_AreRefused()
    {
        var (owner, _) = await OwnerAsync();
        var rice = await ProductAsync(owner);

        var withoutPrice = await SellAsync(owner, rice, 1m, null, "Why");
        var tooLong = await SellAsync(owner, rice, 1m, 8m, new string('x', 201));
        var scale = await SellAsync(owner, rice, 1m, 8.12345m, "Precise");

        Assert.Equal("sale.line_price_reason_without_price", (await ReadAsync(withoutPrice)).GetProperty("code").GetString());
        Assert.Equal("sale.line_override_reason_too_long", (await ReadAsync(tooLong)).GetProperty("code").GetString());
        Assert.Equal("money.scale_exceeded", (await ReadAsync(scale)).GetProperty("code").GetString());
        Assert.Equal(100m, await StockAsync(owner, rice));
    }

    // ---- who may

    [Fact]
    public async Task AStaffCashier_CannotStateAPrice_AndNothingIsRecorded()
    {
        var (owner, organization) = await OwnerAsync();
        var staff = await MemberAsync(organization, Role.Staff);
        var rice = await ProductAsync(owner);
        var saleId = Guid.CreateVersion7();

        var attempt = await SellAsync(staff, rice, 1m, unitPrice: 5m, reason: "Friend", saleId: saleId);
        var samePriceAttempt = await SellAsync(staff, rice, 1m, unitPrice: 10m, saleId: saleId);
        var catalogSale = await SellAsync(staff, rice, 1m, saleId: saleId);

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, samePriceAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.Created, catalogSale.StatusCode); // the refused attempts did not reserve the id
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.sale_price_overridden"));
    }

    [Fact]
    public async Task AManager_CanDiscount_ButNotBelowCost_AndTheOwnerCan()
    {
        var (owner, organization) = await OwnerAsync();
        var manager = await MemberAsync(organization, Role.Manager);
        var rice = await ProductAsync(owner, price: 10m, cost: 6m);

        var atCost = await SellAsync(manager, rice, 1m, unitPrice: 6m, reason: "Clearance");
        var belowCost = await SellAsync(manager, rice, 1m, unitPrice: 5.9999m, reason: "Clearance");
        var ownerBelowCost = await SellAsync(owner, rice, 1m, unitPrice: 1m, reason: "Damaged stock");

        Assert.Equal(HttpStatusCode.Created, atCost.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, belowCost.StatusCode);
        var problem = await ReadAsync(belowCost);
        Assert.Equal("sale.line_price_below_cost", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("6", problem.GetProperty("detail").GetString()!); // the message does not reveal the cost
        Assert.Equal(HttpStatusCode.Created, ownerBelowCost.StatusCode);
        Assert.Equal(98m, await StockAsync(owner, rice));
    }

    [Fact]
    public async Task AnAnonymousCaller_IsRefused()
    {
        using var client = NewClient(_factory);

        var response = await SellAsync(client, Guid.CreateVersion7(), 1m, 5m, "x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- audit

    [Fact]
    public async Task AnOverride_IsAuditedWithItsCountsAndAmounts_ButNeverItsReasonText()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, price: 10m, cost: 6m);
        var sold = (await ReadAsync(await SellAsync(owner, rice, 3m, unitPrice: 5m, reason: "Secret reason text"))).GetProperty("sale").GetProperty("id").GetGuid();
        var plain = (await ReadAsync(await SellAsync(owner, rice, 1m))).GetProperty("sale").GetProperty("id").GetGuid();

        await using var db = _database.CreateContext(organization);
        var events = await db.Set<AuditEvent>().Where(e => e.Action == "sales.sale_price_overridden").ToListAsync();

        var audit = Assert.Single(events);
        Assert.Equal((sold, "Sale"), (audit.EntityId, audit.EntityType));
        Assert.NotNull(audit.ActorId);
        Assert.Contains("1 of 1 line(s)", audit.Description!);
        Assert.Contains("15", audit.Description!); // catalog total 30 minus charged total 15
        Assert.Contains("1 below cost", audit.Description!);
        Assert.DoesNotContain("Secret", audit.Description!);
        Assert.NotEqual(plain, audit.EntityId);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.sale_created" && e.EntityId == sold));
    }

    // ---- retries

    [Fact]
    public async Task RepeatingAnOverrideSale_ChangesNothing_AndADifferentPriceOrReasonIsAConflict()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, price: 10m);
        var saleId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await SellAsync(owner, rice, 2m, 8m, "Promo", saleId)).StatusCode);

        var repeat = await SellAsync(owner, rice, 2m, 8m, "Promo", saleId);
        var otherPrice = await SellAsync(owner, rice, 2m, 7m, "Promo", saleId);
        var otherReason = await SellAsync(owner, rice, 2m, 8m, "Another", saleId);
        var catalogPrice = await SellAsync(owner, rice, 2m, null, null, saleId);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.True((await ReadAsync(repeat)).GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, otherPrice.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, otherReason.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, catalogPrice.StatusCode);
        Assert.Equal(98m, await StockAsync(owner, rice));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.sale_price_overridden"));
    }

    [Fact]
    public async Task ACatalogSaleRepeatedWithAPrice_IsAConflict()
    {
        var (owner, _) = await OwnerAsync();
        var rice = await ProductAsync(owner, price: 10m);
        var saleId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await SellAsync(owner, rice, 1m, null, null, saleId)).StatusCode);

        var withPrice = await SellAsync(owner, rice, 1m, 8m, "Promo", saleId);

        Assert.Equal(HttpStatusCode.Conflict, withPrice.StatusCode);
    }

    // ---- consistency

    [Fact]
    public async Task TheStoredLinesAndTheSaleTotal_ReconcileWithTheChargedPrices()
    {
        var (owner, organization) = await OwnerAsync();
        var rice = await ProductAsync(owner, price: 10m, cost: 6m);
        var oil = await ProductAsync(owner, price: 99.99m, cost: 80m);
        var response = await PostAsync(owner, "/api/v1/sales", new
        {
            lines = new object[]
            {
                new { productId = rice, quantity = 3.3333m, unitPrice = 7.77m, priceReason = "Bulk" },
                new { productId = oil, quantity = 2m },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = (await ReadAsync(response)).GetProperty("sale");

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(round(\"UnitPrice\" * \"Quantity\", 4)), 0) FROM \"SaleLines\" WHERE \"OrganizationId\" = @org AND \"SaleId\" = @sale";
        command.Parameters.AddWithValue("org", organization);
        command.Parameters.AddWithValue("sale", sale.GetProperty("id").GetGuid());
        var stored = (decimal)(await command.ExecuteScalarAsync())!;

        Assert.Equal(stored, sale.GetProperty("total").GetDecimal());
        Assert.Equal(25.8997m + 199.98m, stored); // 3.3333 x 7.77 = 25.899741 -> 25.8997 (rounded per line), 2 x 99.99
    }

    [Fact]
    public async Task AnotherOrganizationsProduct_CannotBeSoldWithAnOverride()
    {
        var (first, _) = await OwnerAsync();
        var (second, _) = await OwnerAsync();
        var theirs = await ProductAsync(second);

        var response = await SellAsync(first, theirs, 1m, 1m, "Sneaky");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(100m, await StockAsync(second, theirs));
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsThePriceFields()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var line = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("SaleLineRequest").GetProperty("properties");
        Assert.True(line.TryGetProperty("unitPrice", out _));
        Assert.True(line.TryGetProperty("priceReason", out _));
    }
}
