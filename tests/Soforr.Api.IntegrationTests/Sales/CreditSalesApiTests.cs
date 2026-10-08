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
/// Selling on account (CUSTOMER-002, part 1) through the running application against real PostgreSQL: a customer, the amount
/// paid now, the due date fixed at the time of the sale, the walk-in rule, over-payment, inactive and foreign customers,
/// repeating a sale id, and voiding a sale that has money on it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CreditSalesApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public CreditSalesApiTests(PostgresDatabaseFixture database)
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

    /// <summary>A product priced 10 with the given stock.</summary>
    private static async Task<Guid> ProductAsync(HttpClient owner, decimal stock = 100m, string name = "Rice")
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = "SKU-" + Guid.NewGuid().ToString("N"), name, unitOfMeasure = "bag", sellingPrice = 10, costPrice = 6 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(owner, "/api/v1/inventory/adjustments", new { productId = id, quantityChange = stock, note = "Opening stock" })).StatusCode);
        return id;
    }

    private static async Task<Guid> CustomerAsync(HttpClient owner, int? terms = 30, string name = "Awa Jallow")
    {
        var response = await PostAsync(owner, "/api/v1/customers", new { name, contactDetails = "+220 555 0100", creditTermsDays = terms });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SellAsync(HttpClient client, Guid product, decimal quantity, Guid? customer = null, decimal? amountPaid = null, string? method = null, Guid? saleId = null) =>
        PostAsync(client, "/api/v1/sales", new { saleId, customerId = customer, amountPaid, paymentMethod = method, lines = new[] { new { productId = product, quantity } } });

    private static async Task<decimal> StockAsync(HttpClient client, Guid product) =>
        (await ReadAsync(await client.GetAsync(Relative($"/api/v1/inventory/stock/{product}")))).GetProperty("quantity").GetDecimal();

    private static JsonElement SaleOf(JsonElement result) => result.GetProperty("sale");

    // ---- selling on account

    [Fact]
    public async Task ASaleOnAccount_PaidNothing_OwesTheWholeTotal_DueAfterTheCustomersTerms()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner, terms: 30);

        var response = await SellAsync(owner, product, 5m, customer, amountPaid: 0m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = SaleOf(await ReadAsync(response));
        Assert.Equal(customer, sale.GetProperty("customerId").GetGuid());
        Assert.Equal((50m, 0m, 50m), (sale.GetProperty("total").GetDecimal(), sale.GetProperty("amountPaid").GetDecimal(), sale.GetProperty("outstanding").GetDecimal()));
        Assert.Empty(sale.GetProperty("payments").EnumerateArray());
        var created = sale.GetProperty("createdAtUtc").GetDateTimeOffset();
        Assert.Equal(created.AddDays(30), sale.GetProperty("dueAtUtc").GetDateTimeOffset());
        Assert.Equal(95m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task APartPayment_IsRecordedWithTheSale_UnderTheSalesOwnId_WithItsMethod()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);

        var response = await SellAsync(owner, product, 10m, customer, amountPaid: 40m, method: "  Mobile money  ");

        var result = await ReadAsync(response);
        var sale = SaleOf(result);
        var payment = Assert.Single(sale.GetProperty("payments").EnumerateArray());
        Assert.Equal(result.GetProperty("saleId").GetGuid(), payment.GetProperty("id").GetGuid());
        Assert.Equal(("Payment", 40m, "Mobile money"), (payment.GetProperty("kind").GetString(), payment.GetProperty("amount").GetDecimal(), payment.GetProperty("methodLabel").GetString()));
        Assert.Equal((40m, 60m), (sale.GetProperty("amountPaid").GetDecimal(), sale.GetProperty("outstanding").GetDecimal()));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync());
    }

    [Fact]
    public async Task WhenNoAmountIsStated_ASaleOnAccountIsPaidInFull_SoNoDebtIsCreatedByAccident()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);

        var sale = SaleOf(await ReadAsync(await SellAsync(owner, product, 3m, customer)));

        Assert.Equal((30m, 0m), (sale.GetProperty("amountPaid").GetDecimal(), sale.GetProperty("outstanding").GetDecimal()));
        Assert.Equal(30m, Assert.Single(sale.GetProperty("payments").EnumerateArray()).GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task ACustomerWithoutTerms_IsDueImmediately()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner, terms: null);

        var sale = SaleOf(await ReadAsync(await SellAsync(owner, product, 1m, customer, amountPaid: 0m)));

        Assert.Equal(sale.GetProperty("createdAtUtc").GetDateTimeOffset(), sale.GetProperty("dueAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task ChangingTheCustomersTermsLater_DoesNotMoveTheDueDateOfAnExistingDebt()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner, terms: 30);
        var saleId = (await ReadAsync(await SellAsync(owner, product, 1m, customer, amountPaid: 0m))).GetProperty("saleId").GetGuid();
        var before = (await ReadAsync(await owner.GetAsync(Relative($"/api/v1/sales/{saleId}")))).GetProperty("dueAtUtc").GetDateTimeOffset();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(owner, HttpMethod.Put, $"/api/v1/customers/{customer}", new { name = "Awa Jallow", creditTermsDays = 3 })).StatusCode);

        var after = (await ReadAsync(await owner.GetAsync(Relative($"/api/v1/sales/{saleId}")))).GetProperty("dueAtUtc").GetDateTimeOffset();
        Assert.Equal(before, after);
    }

    // ---- walk-in sales

    [Fact]
    public async Task AWalkInSale_IsPaidInFull_HasNoCustomerDueDateOrBalance()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);

        var sale = SaleOf(await ReadAsync(await SellAsync(owner, product, 2m)));

        Assert.Equal(JsonValueKind.Null, sale.GetProperty("customerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, sale.GetProperty("dueAtUtc").ValueKind);
        Assert.Equal(0m, sale.GetProperty("outstanding").GetDecimal());
        Assert.Empty(sale.GetProperty("payments").EnumerateArray());
    }

    [Fact]
    public async Task AWalkInSale_MayStateTheTotalAsTheAmountPaid_ButNothingElse()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);

        var exact = await SellAsync(owner, product, 2m, amountPaid: 20m);
        var less = await SellAsync(owner, product, 2m, amountPaid: 5m);
        var zero = await SellAsync(owner, product, 2m, amountPaid: 0m);

        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        foreach (var refused in new[] { less, zero })
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var problem = await ReadAsync(refused);
            Assert.Equal("sale.walk_in_is_paid_in_full", problem.GetProperty("code").GetString());
            Assert.Equal(1, problem.GetProperty("errors").GetProperty("amountPaid").GetArrayLength());
        }

        Assert.Equal(98m, await StockAsync(owner, product)); // only the exact one sold
    }

    // ---- refusing what cannot be

    [Theory]
    [InlineData(50.0001, "sale.payment_exceeds_total")]
    [InlineData(1000, "sale.payment_exceeds_total")]
    [InlineData(-1, "sale.payment_amount_negative")]
    [InlineData(10.00001, "money.scale_exceeded")]
    public async Task AnInvalidAmountPaid_IsRefused_WithTheField_AndNothingIsSold(double amount, string code)
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);

        var response = await SellAsync(owner, product, 5m, customer, amountPaid: (decimal)amount);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("amountPaid").GetArrayLength());
        Assert.Equal(100m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<Sale>().CountAsync());
        Assert.Equal(0, await db.Set<SalePayment>().CountAsync());
    }

    [Fact]
    public async Task APaymentMethodThatIsTooLong_IsRefused()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);

        var response = await SellAsync(owner, product, 1m, customer, amountPaid: 5m, method: new string('x', 51));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("sale.payment_method_too_long", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("paymentMethod").GetArrayLength());
    }

    [Fact]
    public async Task AnEmptyCustomerId_IsRefused()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);

        var response = await SellAsync(owner, product, 1m, Guid.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("sale.customer_invalid", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnUnknownCustomer_IsNotFound_AndNothingIsSold()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);

        var response = await SellAsync(owner, product, 5m, Guid.CreateVersion7(), amountPaid: 0m);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("customer.not_found", problem.GetProperty("code").GetString());
        Assert.Equal(100m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task AnInactiveCustomer_CannotBeSoldToOnAccount()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(owner, $"/api/v1/customers/{customer}/deactivate", null)).StatusCode);

        var response = await SellAsync(owner, product, 5m, customer, amountPaid: 0m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("sale.customer_inactive", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(100m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task AnotherOrganizationsCustomer_IsNotFound()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var (other, _) = await OwnerAsync();
        var theirCustomer = await CustomerAsync(other);

        var response = await SellAsync(owner, product, 5m, theirCustomer, amountPaid: 0m);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("customer.not_found", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(100m, await StockAsync(owner, product));
    }

    [Fact]
    public async Task ASaleThatCannotGetItsStock_LeavesNoSaleAndNoPayment()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 3m);
        var customer = await CustomerAsync(owner);

        var response = await SellAsync(owner, product, 5m, customer, amountPaid: 20m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("stock_level.insufficient_stock", (await ReadAsync(response)).GetProperty("code").GetString());
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<Sale>().CountAsync());
        Assert.Equal(0, await db.Set<SalePayment>().CountAsync());
        Assert.Equal(3m, await StockAsync(owner, product));
    }

    // ---- repeating a sale id

    [Fact]
    public async Task TheSameSaleIdWithTheSameCustomerAndAmount_IsARepeat_AndChangesNothing()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var saleId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await SellAsync(owner, product, 5m, customer, amountPaid: 20m, saleId: saleId)).StatusCode);

        var again = await SellAsync(owner, product, 5m, customer, amountPaid: 20m, saleId: saleId);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var result = await ReadAsync(again);
        Assert.True(result.GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(30m, SaleOf(result).GetProperty("outstanding").GetDecimal());
        Assert.Equal(95m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<Sale>().CountAsync());
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync());
    }

    [Fact]
    public async Task AnAbsentAmountAndTheExplicitTotal_AreTheSameRequest()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var saleId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await SellAsync(owner, product, 5m, customer, saleId: saleId)).StatusCode);

        var again = await SellAsync(owner, product, 5m, customer, amountPaid: 50m, saleId: saleId);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Theory]
    [InlineData("another amount")]
    [InlineData("no amount")]
    [InlineData("another customer")]
    [InlineData("no customer")]
    public async Task TheSameSaleIdWithAnotherSettlement_IsAConflict_AndChangesNothing(string scenario)
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var otherCustomer = await CustomerAsync(owner, name: "Someone Else");
        var saleId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await SellAsync(owner, product, 5m, customer, amountPaid: 20m, saleId: saleId)).StatusCode);

        var response = scenario switch
        {
            "another amount" => await SellAsync(owner, product, 5m, customer, amountPaid: 25m, saleId: saleId),
            "no amount" => await SellAsync(owner, product, 5m, customer, saleId: saleId),
            "another customer" => await SellAsync(owner, product, 5m, otherCustomer, amountPaid: 20m, saleId: saleId),
            _ => await SellAsync(owner, product, 5m, saleId: saleId),
        };

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("sale.id_conflict", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(95m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<Sale>().CountAsync());
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync());
    }

    [Fact]
    public async Task ARepeat_StillSucceeds_AfterTheCustomerWasDeactivated()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var saleId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await SellAsync(owner, product, 5m, customer, amountPaid: 0m, saleId: saleId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(owner, $"/api/v1/customers/{customer}/deactivate", null)).StatusCode);

        var again = await SellAsync(owner, product, 5m, customer, amountPaid: 0m, saleId: saleId);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await ReadAsync(again)).GetProperty("alreadyRecorded").GetBoolean());
    }

    // ---- voiding

    [Fact]
    public async Task ASaleWithMoneyOnIt_CannotBeVoided_AndKeepsItsStockAndPayment()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var saleId = (await ReadAsync(await SellAsync(owner, product, 5m, customer, amountPaid: 20m))).GetProperty("saleId").GetGuid();

        var response = await PostAsync(owner, $"/api/v1/sales/{saleId}/void", new { reason = "Mistake" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("sale.has_payments", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(95m, await StockAsync(owner, product));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(SaleStatus.Completed, (await db.Set<Sale>().SingleAsync()).Status);
        Assert.Equal(0, await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.Return));
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.sale_voided"));
    }

    [Fact]
    public async Task AnUnpaidSaleOnAccount_CanBeVoided_AndThenOwesNothing()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var saleId = (await ReadAsync(await SellAsync(owner, product, 5m, customer, amountPaid: 0m))).GetProperty("saleId").GetGuid();

        var response = await PostAsync(owner, $"/api/v1/sales/{saleId}/void", new { reason = "Customer cancelled" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sale = await ReadAsync(response);
        Assert.Equal("Voided", sale.GetProperty("status").GetString());
        Assert.Equal(0m, sale.GetProperty("outstanding").GetDecimal());
        Assert.Equal(100m, await StockAsync(owner, product));
    }

    // ---- reading

    [Fact]
    public async Task TheSaleAndTheList_ShowTheCustomerTheDueDateAndWhatIsOwed()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var saleId = (await ReadAsync(await SellAsync(owner, product, 5m, customer, amountPaid: 15m, method: "Cash"))).GetProperty("saleId").GetGuid();

        var read = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/sales/{saleId}")));
        var list = await ReadAsync(await owner.GetAsync(Relative("/api/v1/sales")));

        Assert.Equal((customer, 35m, 15m), (read.GetProperty("customerId").GetGuid(), read.GetProperty("outstanding").GetDecimal(), read.GetProperty("amountPaid").GetDecimal()));
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal((customer, 35m), (item.GetProperty("customerId").GetGuid(), item.GetProperty("outstanding").GetDecimal()));
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("dueAtUtc").ValueKind);
    }

    [Fact]
    public async Task AStaffCashier_CanSellOnAccount_AndTheRecordShowsWhoTookThePayment()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var staff = await SignInAsMemberAsync(_factory, _database, organization, Role.Staff);
        _clients.Add(staff);

        var response = await SellAsync(staff, product, 2m, customer, amountPaid: 5m, method: "Cash");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = SaleOf(await ReadAsync(response));
        var payment = Assert.Single(sale.GetProperty("payments").EnumerateArray());
        Assert.Equal(sale.GetProperty("createdByUserId").GetGuid(), payment.GetProperty("recordedByUserId").GetGuid());
        Assert.Equal(15m, sale.GetProperty("outstanding").GetDecimal());
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsTheCustomerAndTheAmountPaid()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var request = schemas.GetProperty("SaleRequest").GetProperty("properties");
        Assert.True(request.TryGetProperty("customerId", out _));
        Assert.True(request.TryGetProperty("amountPaid", out _));
        Assert.True(request.TryGetProperty("paymentMethod", out _));
        var dto = schemas.GetProperty("SaleDto").GetProperty("properties");
        foreach (var name in new[] { "customerId", "dueAtUtc", "amountPaid", "outstanding", "payments" })
        {
            Assert.True(dto.TryGetProperty(name, out _), name);
        }

        Assert.True(schemas.TryGetProperty("SalePaymentDto", out _));
        Assert.False(schemas.TryGetProperty("SalePayment", out _));
    }
}
