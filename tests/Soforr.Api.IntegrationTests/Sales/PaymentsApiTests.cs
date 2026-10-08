using System.Globalization;
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
/// Recording and reversing payments, and the customer balance, through the running application against real PostgreSQL
/// (CUSTOMER-002, part 2): the rules (never more than is owed, never on a walk-in or voided sale), repeating a payment id,
/// parallel payments, who may do what, tenant isolation, the audit trail and the balance derived from the records.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PaymentsApiTests : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private readonly PostgresDatabaseFixture _database;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public PaymentsApiTests(PostgresDatabaseFixture database)
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

    private static async Task<Guid> ProductAsync(HttpClient owner, decimal stock = 1000m)
    {
        var response = await PostAsync(owner, "/api/v1/products", new { sku = "SKU-" + Guid.NewGuid().ToString("N"), name = "Rice", unitOfMeasure = "bag", sellingPrice = 10, costPrice = 6 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(owner, "/api/v1/inventory/adjustments", new { productId = id, quantityChange = stock, note = "Opening stock" })).StatusCode);
        return id;
    }

    private static async Task<Guid> CustomerAsync(HttpClient owner, int? terms = 30, string name = "Awa Jallow")
    {
        var response = await PostAsync(owner, "/api/v1/customers", new { name, creditTermsDays = terms });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>A sale of 10 bags (total 100) on account, with the given amount paid now.</summary>
    private static async Task<Guid> SaleOnAccountAsync(HttpClient client, Guid product, Guid customer, decimal paidNow = 0m, decimal quantity = 10m)
    {
        var response = await PostAsync(client, "/api/v1/sales", new { customerId = customer, amountPaid = paidNow, lines = new[] { new { productId = product, quantity } } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("saleId").GetGuid();
    }

    private static Task<HttpResponseMessage> PayAsync(HttpClient client, Guid sale, decimal? amount, Guid? paymentId = null, DateTimeOffset? paidAt = null, string? method = "Cash") =>
        PostAsync(client, $"/api/v1/sales/{sale}/payments", new { paymentId, amount, paidAtUtc = paidAt, method });

    private static Task<HttpResponseMessage> ReverseAsync(HttpClient client, Guid sale, Guid payment, string? reason = "Entered by mistake", Guid? reversalId = null) =>
        PostAsync(client, $"/api/v1/sales/{sale}/payments/{payment}/reverse", new { reversalId, reason });

    private static async Task<JsonElement> SaleAsync(HttpClient client, Guid sale) =>
        await ReadAsync(await client.GetAsync(Relative($"/api/v1/sales/{sale}")));

    private static decimal Outstanding(JsonElement sale) => sale.GetProperty("outstanding").GetDecimal();

    private async Task<decimal> RawAmountPaidAsync(Guid sale)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(CASE WHEN \"Kind\" = 'Payment' THEN \"Amount\" ELSE -\"Amount\" END), 0) FROM \"SalePayments\" WHERE \"SaleId\" = @id";
        command.Parameters.AddWithValue("id", sale);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    // ---- recording a payment

    [Fact]
    public async Task APayment_ReducesWhatIsOwed_IsRecordedWithItsDetails_AndAudited()
    {
        var (owner, organization) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));

        var response = await PayAsync(owner, sale, 30m, method: "  Mobile money  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/v1/sales/{sale}", response.Headers.Location?.OriginalString);
        var result = await ReadAsync(response);
        Assert.False(result.GetProperty("alreadyRecorded").GetBoolean());
        var body = result.GetProperty("sale");
        Assert.Equal((30m, 70m), (body.GetProperty("amountPaid").GetDecimal(), Outstanding(body)));
        var payment = Assert.Single(body.GetProperty("payments").EnumerateArray());
        Assert.Equal(result.GetProperty("paymentId").GetGuid(), payment.GetProperty("id").GetGuid());
        Assert.Equal(("Payment", 30m, "Mobile money"), (payment.GetProperty("kind").GetString(), payment.GetProperty("amount").GetDecimal(), payment.GetProperty("methodLabel").GetString()));
        Assert.Equal(70m, Outstanding(await SaleAsync(owner, sale)));

        await using var db = _database.CreateContext(organization);
        var audit = await db.Set<AuditEvent>().SingleAsync(e => e.Action == "sales.payment_recorded");
        Assert.Equal((sale, "Sale", "A payment of 30 was recorded against a sale."), (audit.EntityId, audit.EntityType, audit.Description));
        Assert.DoesNotContain("Mobile", audit.Description);
        Assert.Equal(30m, await RawAmountPaidAsync(sale));
    }

    [Fact]
    public async Task PayingTheRest_SettlesTheSale_AndTheCustomerOwesNothing()
    {
        var (owner, _) = await OwnerAsync();
        var customer = await CustomerAsync(owner);
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), customer, paidNow: 40m);

        Assert.Equal(HttpStatusCode.Created, (await PayAsync(owner, sale, 25m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(owner, sale, 35m)).StatusCode);

        Assert.Equal(0m, Outstanding(await SaleAsync(owner, sale)));
        var balance = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{customer}/balance")));
        Assert.Equal((0m, 0), (balance.GetProperty("totalOutstanding").GetDecimal(), balance.GetProperty("openSaleCount").GetInt32()));
        Assert.Empty(balance.GetProperty("openSales").EnumerateArray());
    }

    [Fact]
    public async Task APaymentLargerThanWhatIsOwed_IsRefused_AndChangesNothing()
    {
        var (owner, organization) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner), paidNow: 40m);

        var response = await PayAsync(owner, sale, 60.0001m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("sale.payment_exceeds_outstanding", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(60m, Outstanding(await SaleAsync(owner, sale)));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync(p => p.SaleId == sale));
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.payment_recorded"));
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(owner, sale, 60m)).StatusCode); // exactly what is owed is fine
    }

    [Theory]
    [InlineData(0, "sale.payment_amount_required")]
    [InlineData(-5, "sale.payment_amount_required")]
    [InlineData(10.00001, "money.scale_exceeded")]
    public async Task AnInvalidAmount_Returns400_WithTheField(double amount, string code)
    {
        var (owner, _) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));

        var response = await PayAsync(owner, sale, (decimal)amount);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("amount").GetArrayLength());
        Assert.Equal(100m, Outstanding(await SaleAsync(owner, sale)));
    }

    [Fact]
    public async Task ARequestWithoutAnAmount_Returns400()
    {
        var (owner, _) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));

        var response = await PayAsync(owner, sale, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("sale.payment_amount_required", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ThePaymentDate_MustBeBetweenTheSaleAndNow_AndIsKeptWhenValid()
    {
        var (owner, _) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var soldAt = (await SaleAsync(owner, sale)).GetProperty("createdAtUtc").GetDateTimeOffset();

        var beforeSale = await PayAsync(owner, sale, 10m, paidAt: soldAt.AddMinutes(-1));
        var inFuture = await PayAsync(owner, sale, 10m, paidAt: DateTimeOffset.UtcNow.AddDays(1));
        var valid = await PayAsync(owner, sale, 10m, paidAt: soldAt.AddMilliseconds(1));

        foreach (var (response, code) in new[] { (beforeSale, "sale.payment_before_sale"), (inFuture, "sale.payment_in_future") })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await ReadAsync(response);
            Assert.Equal(code, problem.GetProperty("code").GetString());
            Assert.Equal(1, problem.GetProperty("errors").GetProperty("paidAtUtc").GetArrayLength());
        }

        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        var payment = (await ReadAsync(valid)).GetProperty("sale").GetProperty("payments")[0];
        Assert.Equal(soldAt.AddMilliseconds(1), payment.GetProperty("paidAtUtc").GetDateTimeOffset());
        Assert.True(payment.GetProperty("recordedAtUtc").GetDateTimeOffset() > payment.GetProperty("paidAtUtc").GetDateTimeOffset());
        Assert.Equal(90m, Outstanding(await SaleAsync(owner, sale)));
    }

    [Fact]
    public async Task AMethodLabelThatIsTooLong_IsRefused_AndOneThatIsBlankIsLeftOut()
    {
        var (owner, _) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));

        var tooLong = await PayAsync(owner, sale, 5m, method: new string('x', 51));
        var blank = await PayAsync(owner, sale, 5m, method: "   ");

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var problem = await ReadAsync(tooLong);
        Assert.Equal("sale.payment_method_too_long", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("method").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(blank)).GetProperty("sale").GetProperty("payments")[0].GetProperty("methodLabel").ValueKind);
    }

    [Fact]
    public async Task AWalkInSale_AnUnknownSale_AndAVoidedSale_CannotBePaid()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var walkIn = (await ReadAsync(await PostAsync(owner, "/api/v1/sales", new { lines = new[] { new { productId = product, quantity = 1 } } }))).GetProperty("saleId").GetGuid();
        var voided = await SaleOnAccountAsync(owner, product, customer);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(owner, $"/api/v1/sales/{voided}/void", new { reason = "Cancelled" })).StatusCode);

        var onWalkIn = await PayAsync(owner, walkIn, 5m);
        var onVoided = await PayAsync(owner, voided, 5m);
        var unknown = await PayAsync(owner, Guid.CreateVersion7(), 5m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, onWalkIn.StatusCode);
        Assert.Equal("sale.not_payable", (await ReadAsync(onWalkIn)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onVoided.StatusCode);
        Assert.Equal("sale.not_payable", (await ReadAsync(onVoided)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("sale.not_found", (await ReadAsync(unknown)).GetProperty("code").GetString());
    }

    // ---- repeating a payment id

    [Fact]
    public async Task TheSamePaymentIdWithTheSamePayment_IsARepeat_AndChangesNothing()
    {
        var (owner, organization) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var id = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(owner, sale, 30m, id)).StatusCode);

        var again = await PayAsync(owner, sale, 30m, id);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var result = await ReadAsync(again);
        Assert.True(result.GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(70m, Outstanding(result.GetProperty("sale")));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync(p => p.SaleId == sale));
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.payment_recorded"));
    }

    [Fact]
    public async Task ARepeat_WithTheSameDateAfterTheSaleIsFullyPaid_IsStillARepeat_NotAnOverpayment()
    {
        var (owner, _) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var id = Guid.CreateVersion7();
        var paidAt = (await SaleAsync(owner, sale)).GetProperty("createdAtUtc").GetDateTimeOffset().AddMilliseconds(1);
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(owner, sale, 100m, id, paidAt)).StatusCode);

        var again = await PayAsync(owner, sale, 100m, id, paidAt);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await ReadAsync(again)).GetProperty("alreadyRecorded").GetBoolean());
    }

    [Fact]
    public async Task ARepeatWithADateFinerThanTheDatabaseKeeps_IsStillARepeat()
    {
        var (owner, _) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var soldAt = (await SaleAsync(owner, sale)).GetProperty("createdAtUtc").GetDateTimeOffset();
        var id = Guid.CreateVersion7();
        var fine = soldAt.AddMilliseconds(1).AddTicks(7); // 100-nanosecond ticks; the database stores microseconds
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(owner, sale, 30m, id, fine)).StatusCode);

        var again = await PayAsync(owner, sale, 30m, id, fine);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await ReadAsync(again)).GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(70m, Outstanding(await SaleAsync(owner, sale)));
    }

    [Theory]
    [InlineData("another amount")]
    [InlineData("another method")]
    [InlineData("another date")]
    [InlineData("another sale")]
    public async Task TheSamePaymentIdWithADifferentPayment_IsAConflict_AndChangesNothing(string scenario)
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var sale = await SaleOnAccountAsync(owner, product, customer);
        var otherSale = await SaleOnAccountAsync(owner, product, customer);
        var soldAt = (await SaleAsync(owner, sale)).GetProperty("createdAtUtc").GetDateTimeOffset();
        var id = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(owner, sale, 30m, id, soldAt.AddMilliseconds(1), "Cash")).StatusCode);

        var response = scenario switch
        {
            "another amount" => await PayAsync(owner, sale, 31m, id, soldAt.AddMilliseconds(1), "Cash"),
            "another method" => await PayAsync(owner, sale, 30m, id, soldAt.AddMilliseconds(1), "Mobile money"),
            "another date" => await PayAsync(owner, sale, 30m, id, soldAt.AddMilliseconds(2), "Cash"),
            _ => await PayAsync(owner, otherSale, 30m, id, soldAt.AddMilliseconds(1), "Cash"),
        };

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("sale.payment_id_conflict", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(70m, Outstanding(await SaleAsync(owner, sale)));
        Assert.Equal(100m, Outstanding(await SaleAsync(owner, otherSale)));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync());
    }

    [Fact]
    public async Task ThePaymentIdOfAnotherOrganization_IsAnIndependentPayment()
    {
        var (first, _) = await OwnerAsync();
        var (second, _) = await OwnerAsync();
        var firstSale = await SaleOnAccountAsync(first, await ProductAsync(first), await CustomerAsync(first));
        var secondSale = await SaleOnAccountAsync(second, await ProductAsync(second), await CustomerAsync(second));
        var id = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(first, firstSale, 30m, id)).StatusCode);

        var response = await PayAsync(second, secondSale, 50m, id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False((await ReadAsync(response)).GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(70m, Outstanding(await SaleAsync(first, firstSale)));
        Assert.Equal(50m, Outstanding(await SaleAsync(second, secondSale)));
    }

    [Fact]
    public async Task ARefusedPayment_DoesNotReserveItsId()
    {
        var (owner, _) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var id = Guid.CreateVersion7();

        var refused = await PayAsync(owner, sale, 500m, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var retried = await PayAsync(owner, sale, 50m, id);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(50m, Outstanding(await SaleAsync(owner, sale)));
    }

    // ---- parallel payments

    [Fact]
    public async Task ManyIdenticalPaymentsAtOnce_RecordExactlyOne()
    {
        var (owner, organization) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var id = Guid.CreateVersion7();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PayAsync(owner, sale, 40m, id)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var other in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            // A loser sees the stored payment (200) or, if its retries run out, a plain concurrency conflict; never an id conflict.
            Assert.True(other.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
            if (other.StatusCode == HttpStatusCode.Conflict)
            {
                Assert.Equal("sale.concurrency_conflict", (await ReadAsync(other)).GetProperty("code").GetString());
            }
        }

        Assert.Equal(60m, Outstanding(await SaleAsync(owner, sale)));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync(p => p.SaleId == sale));
    }

    [Fact]
    public async Task ParallelPayments_NeverPayMoreThanIsOwed()
    {
        var (owner, organization) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));

        // Each payment alone is fine; together eight of them would be 240 against a debt of 100.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PayAsync(owner, sale, 30m)));

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.InRange(succeeded, 1, 3);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.True(r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict));
        var paid = await RawAmountPaidAsync(sale);
        Assert.Equal(succeeded * 30m, paid);
        Assert.True(paid <= 100m);
        Assert.Equal(100m - paid, Outstanding(await SaleAsync(owner, sale)));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(succeeded, await db.Set<SalePayment>().CountAsync(p => p.SaleId == sale));
    }

    // ---- reversing a payment

    [Fact]
    public async Task AReversal_PutsTheAmountBackOnWhatIsOwed_AsANewRecord_AndLeavesThePaymentUntouched()
    {
        var (owner, organization) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var paymentId = (await ReadAsync(await PayAsync(owner, sale, 70m))).GetProperty("paymentId").GetGuid();

        var response = await ReverseAsync(owner, sale, paymentId, "  Entered by mistake  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await ReadAsync(response)).GetProperty("sale");
        Assert.Equal((0m, 100m), (body.GetProperty("amountPaid").GetDecimal(), Outstanding(body)));
        var payments = body.GetProperty("payments").EnumerateArray().ToArray();
        Assert.Equal(["Payment", "Reversal"], payments.Select(p => p.GetProperty("kind").GetString()!).ToArray());
        var reversal = payments[1];
        Assert.Equal((70m, paymentId, "Entered by mistake"), (reversal.GetProperty("amount").GetDecimal(), reversal.GetProperty("reversesPaymentId").GetGuid(), reversal.GetProperty("reversalReason").GetString()));
        Assert.Equal((70m, JsonValueKind.Null), (payments[0].GetProperty("amount").GetDecimal(), payments[0].GetProperty("reversesPaymentId").ValueKind));
        Assert.Equal(0m, await RawAmountPaidAsync(sale));

        await using var db = _database.CreateContext(organization);
        var audit = await db.Set<AuditEvent>().SingleAsync(e => e.Action == "sales.payment_reversed");
        Assert.Equal((sale, "A payment was reversed."), (audit.EntityId, audit.Description));
        Assert.DoesNotContain("mistake", audit.Description);
    }

    [Fact]
    public async Task AReversedPayment_CanBePaidAgain_AndTheSaleCanThenBeVoidedOnceItsMoneyIsReversed()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 100m);
        var sale = await SaleOnAccountAsync(owner, product, await CustomerAsync(owner), paidNow: 100m);
        var atSale = sale; // the payment taken with the sale has the sale's own id

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(owner, $"/api/v1/sales/{sale}/void", new { reason = "Mistake" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(owner, sale, atSale, "Refunded in cash")).StatusCode);
        Assert.Equal(100m, Outstanding(await SaleAsync(owner, sale)));
        Assert.Equal(HttpStatusCode.Created, (await PayAsync(owner, sale, 100m)).StatusCode);
        Assert.Equal(0m, Outstanding(await SaleAsync(owner, sale)));
    }

    [Fact]
    public async Task OnceEveryPaymentIsReversed_TheSaleCanBeVoided_AndTheStockComesBack()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner, stock: 100m);
        var sale = await SaleOnAccountAsync(owner, product, await CustomerAsync(owner), paidNow: 100m);
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(owner, sale, sale, "Refund")).StatusCode);

        var voided = await PostAsync(owner, $"/api/v1/sales/{sale}/void", new { reason = "Cancelled after refund" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var body = await ReadAsync(voided);
        Assert.Equal(("Voided", 0m), (body.GetProperty("status").GetString(), Outstanding(body)));
        var stock = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/inventory/stock/{product}")));
        Assert.Equal(100m, stock.GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task AReversalNeedsAReason_AnExistingPayment_AndIsOnlyPossibleOnce()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var sale = await SaleOnAccountAsync(owner, product, customer);
        var otherSale = await SaleOnAccountAsync(owner, product, customer);
        var paymentId = (await ReadAsync(await PayAsync(owner, sale, 10m))).GetProperty("paymentId").GetGuid();

        var noReason = await ReverseAsync(owner, sale, paymentId, "   ");
        var unknown = await ReverseAsync(owner, sale, Guid.CreateVersion7());
        var wrongSale = await ReverseAsync(owner, otherSale, paymentId);
        var first = await ReverseAsync(owner, sale, paymentId, "Wrong");
        var again = await ReverseAsync(owner, sale, paymentId, "Again");
        var reversalId = (await ReadAsync(first)).GetProperty("paymentId").GetGuid();
        var reverseTheReversal = await ReverseAsync(owner, sale, reversalId, "Undo");

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        var problem = await ReadAsync(noReason);
        Assert.Equal("sale.reversal_reason_required", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("reason").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("sale.payment_not_found", (await ReadAsync(unknown)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, wrongSale.StatusCode);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("sale.payment_already_reversed", (await ReadAsync(again)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reverseTheReversal.StatusCode);
        Assert.Equal("sale.payment_not_reversible", (await ReadAsync(reverseTheReversal)).GetProperty("code").GetString());
        Assert.Equal(100m, Outstanding(await SaleAsync(owner, sale)));
    }

    [Fact]
    public async Task TheSameReversalId_IsARepeatForTheSameReversal_AndAConflictForADifferentOne()
    {
        var (owner, organization) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner);
        var sale = await SaleOnAccountAsync(owner, product, customer);
        var other = await SaleOnAccountAsync(owner, product, customer);
        var paymentId = (await ReadAsync(await PayAsync(owner, sale, 10m))).GetProperty("paymentId").GetGuid();
        var otherPayment = (await ReadAsync(await PayAsync(owner, other, 10m))).GetProperty("paymentId").GetGuid();
        var id = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(owner, sale, paymentId, "Wrong", id)).StatusCode);

        var repeat = await ReverseAsync(owner, sale, paymentId, "Wrong", id);
        var otherReason = await ReverseAsync(owner, sale, paymentId, "A different reason", id);
        var otherTarget = await ReverseAsync(owner, other, otherPayment, "Wrong", id);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.True((await ReadAsync(repeat)).GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, otherReason.StatusCode);
        Assert.Equal("sale.payment_id_conflict", (await ReadAsync(otherReason)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, otherTarget.StatusCode);
        Assert.Equal(100m, Outstanding(await SaleAsync(owner, sale)));
        Assert.Equal(90m, Outstanding(await SaleAsync(owner, other)));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.payment_reversed"));
    }

    [Fact]
    public async Task ManyReversalsOfOnePaymentAtOnce_ReverseItExactlyOnce()
    {
        var (owner, organization) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var paymentId = (await ReadAsync(await PayAsync(owner, sale, 60m))).GetProperty("paymentId").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ReverseAsync(owner, sale, paymentId, "Wrong")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.True(r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict));
        Assert.Equal(0m, await RawAmountPaidAsync(sale));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync(p => p.Kind == SalePaymentKind.Reversal));
    }

    // ---- who may do what

    public enum Operation
    {
        Record,
        Reverse,
        Balance,
    }

    private static async Task<HttpResponseMessage> PerformAsync(HttpClient client, Operation operation, Guid sale, Guid payment, Guid customer) => operation switch
    {
        Operation.Record => await PayAsync(client, sale, 1m),
        Operation.Reverse => await ReverseAsync(client, sale, payment),
        Operation.Balance => await client.GetAsync(Relative($"/api/v1/customers/{customer}/balance")),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    public static TheoryData<string, Operation, HttpStatusCode> Matrix()
    {
        var data = new TheoryData<string, Operation, HttpStatusCode>();
        foreach (var operation in Enum.GetValues<Operation>())
        {
            var success = operation == Operation.Balance ? HttpStatusCode.OK : HttpStatusCode.Created;
            data.Add("Anonymous", operation, HttpStatusCode.Unauthorized);
            data.Add("Owner", operation, success);
            data.Add("Manager", operation, success);
            data.Add("Staff", operation, operation == Operation.Reverse ? HttpStatusCode.Forbidden : success);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryOperation_IsAllowedOrDenied_AsTheMatrixSays(string principal, Operation operation, HttpStatusCode expected)
    {
        var (owner, organization) = await OwnerAsync();
        var customer = await CustomerAsync(owner);
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), customer);
        var payment = (await ReadAsync(await PayAsync(owner, sale, 10m))).GetProperty("paymentId").GetGuid();
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

        var response = await PerformAsync(client, operation, sale, payment, customer);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ADeniedReversal_ChangesNothing_AndWritesNoAuditEvent()
    {
        var (owner, organization) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var payment = (await ReadAsync(await PayAsync(owner, sale, 40m))).GetProperty("paymentId").GetGuid();
        var staff = await SignInAsMemberAsync(_factory, _database, organization, Role.Staff);
        _clients.Add(staff);

        var denied = await ReverseAsync(staff, sale, payment);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("identity.forbidden", (await ReadAsync(denied)).GetProperty("code").GetString());
        Assert.Equal(60m, Outstanding(await SaleAsync(owner, sale)));
        await using var db = _database.CreateContext(organization);
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == "sales.payment_reversed"));
    }

    [Fact]
    public async Task AStaffCashier_TakesAPayment_AndTheRecordNamesThem()
    {
        var (owner, organization) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var staff = await SignInAsMemberAsync(_factory, _database, organization, Role.Staff);
        _clients.Add(staff);

        var response = await PayAsync(staff, sale, 25m, method: "Cash");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = (await ReadAsync(response)).GetProperty("sale").GetProperty("payments")[0];
        var me = (await ReadAsync(await staff.GetAsync(Relative("/api/v1/auth/me")))).GetProperty("userId").GetGuid();
        Assert.Equal(me, payment.GetProperty("recordedByUserId").GetGuid());
    }

    [Fact]
    public async Task AnotherOrganization_CannotPayOrReverseOrSeeTheBalance()
    {
        var (owner, _) = await OwnerAsync();
        var customer = await CustomerAsync(owner);
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), customer);
        var payment = (await ReadAsync(await PayAsync(owner, sale, 40m))).GetProperty("paymentId").GetGuid();
        var (other, _) = await OwnerAsync();

        var pay = await PayAsync(other, sale, 10m);
        var reverse = await ReverseAsync(other, sale, payment);
        var balance = await other.GetAsync(Relative($"/api/v1/customers/{customer}/balance"));

        Assert.All([pay, reverse, balance], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal("sale.not_found", (await ReadAsync(pay)).GetProperty("code").GetString());
        Assert.Equal("customer.not_found", (await ReadAsync(balance)).GetProperty("code").GetString());
        Assert.Equal(60m, Outstanding(await SaleAsync(owner, sale)));
    }

    [Fact]
    public async Task APostWithoutAnAntiforgeryToken_IsRejected()
    {
        var (owner, _) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));

        var response = await PostAsync(owner, $"/api/v1/sales/{sale}/payments", new { amount = 10 }, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery.invalid", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(100m, Outstanding(await SaleAsync(owner, sale)));
    }

    [Fact]
    public async Task ThereIsNoWayToEditOrDeleteAPayment()
    {
        var (owner, _) = await OwnerAsync();
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), await CustomerAsync(owner));
        var payment = (await ReadAsync(await PayAsync(owner, sale, 40m))).GetProperty("paymentId").GetGuid();

        var attempts = new[]
        {
            await SendAsync(owner, HttpMethod.Put, $"/api/v1/sales/{sale}/payments/{payment}", new { amount = 1 }),
            await SendAsync(owner, HttpMethod.Delete, $"/api/v1/sales/{sale}/payments/{payment}", body: null),
            await SendAsync(owner, HttpMethod.Delete, $"/api/v1/sales/{sale}/payments", body: null),
        };

        Assert.All(attempts, response => Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed));
        Assert.Equal(60m, Outstanding(await SaleAsync(owner, sale)));
    }

    // ---- the customer balance

    [Fact]
    public async Task TheBalance_IsTheSumOfWhatIsOwedOnTheCustomersOpenSales_EarliestDueFirst()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner, terms: 30);
        var settled = await SaleOnAccountAsync(owner, product, customer, paidNow: 100m);
        var partlyPaid = await SaleOnAccountAsync(owner, product, customer, paidNow: 25m, quantity: 10m);
        var unpaid = await SaleOnAccountAsync(owner, product, customer, quantity: 4m);
        var voided = await SaleOnAccountAsync(owner, product, customer, quantity: 50m);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(owner, $"/api/v1/sales/{voided}/void", new { reason = "Cancelled" })).StatusCode);
        var walkIn = await PostAsync(owner, "/api/v1/sales", new { lines = new[] { new { productId = product, quantity = 7 } } });
        var someoneElse = await CustomerAsync(owner, name: "Someone Else");
        await SaleOnAccountAsync(owner, product, someoneElse);
        Assert.Equal(HttpStatusCode.Created, walkIn.StatusCode);

        var balance = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{customer}/balance")));

        Assert.Equal((customer, "Awa Jallow", "GMD"), (balance.GetProperty("customerId").GetGuid(), balance.GetProperty("customerName").GetString(), balance.GetProperty("currency").GetString()));
        Assert.Equal((115m, 2, 0m, 0), (balance.GetProperty("totalOutstanding").GetDecimal(), balance.GetProperty("openSaleCount").GetInt32(), balance.GetProperty("overdueAmount").GetDecimal(), balance.GetProperty("overdueSaleCount").GetInt32()));
        var open = balance.GetProperty("openSales").EnumerateArray().ToArray();
        Assert.Equal([partlyPaid, unpaid], open.Select(s => s.GetProperty("saleId").GetGuid()).ToArray());
        Assert.Equal((100m, 25m, 75m, false), (open[0].GetProperty("total").GetDecimal(), open[0].GetProperty("amountPaid").GetDecimal(), open[0].GetProperty("outstanding").GetDecimal(), open[0].GetProperty("isOverdue").GetBoolean()));
        Assert.DoesNotContain(open, s => s.GetProperty("saleId").GetGuid() == settled);
    }

    [Fact]
    public async Task ASaleThatIsDueImmediately_IsOverdue_AndPayingItClearsTheOverdueAmount()
    {
        var (owner, _) = await OwnerAsync();
        var product = await ProductAsync(owner);
        var customer = await CustomerAsync(owner, terms: 0);
        var sale = await SaleOnAccountAsync(owner, product, customer);

        var before = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{customer}/balance")));
        Assert.Equal((100m, 100m, 1), (before.GetProperty("totalOutstanding").GetDecimal(), before.GetProperty("overdueAmount").GetDecimal(), before.GetProperty("overdueSaleCount").GetInt32()));
        Assert.True(before.GetProperty("openSales")[0].GetProperty("isOverdue").GetBoolean());

        Assert.Equal(HttpStatusCode.Created, (await PayAsync(owner, sale, 100m)).StatusCode);
        var after = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{customer}/balance")));
        Assert.Equal((0m, 0m), (after.GetProperty("totalOutstanding").GetDecimal(), after.GetProperty("overdueAmount").GetDecimal()));
    }

    [Fact]
    public async Task AReversedPayment_ComesBackOntoTheBalance()
    {
        var (owner, _) = await OwnerAsync();
        var customer = await CustomerAsync(owner);
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), customer, paidNow: 100m);
        Assert.Equal(0m, (await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{customer}/balance")))).GetProperty("totalOutstanding").GetDecimal());

        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(owner, sale, sale, "Bounced")).StatusCode);

        var balance = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{customer}/balance")));
        Assert.Equal((100m, 1), (balance.GetProperty("totalOutstanding").GetDecimal(), balance.GetProperty("openSaleCount").GetInt32()));
    }

    [Fact]
    public async Task ACustomerWithNoSales_OwesNothing_AndAnInactiveCustomersBalanceCanStillBeRead()
    {
        var (owner, _) = await OwnerAsync();
        var customer = await CustomerAsync(owner);
        var sale = await SaleOnAccountAsync(owner, await ProductAsync(owner), customer);
        var empty = await CustomerAsync(owner, name: "No Sales Yet");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(owner, $"/api/v1/customers/{customer}/deactivate", null)).StatusCode);

        var none = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{empty}/balance")));
        var inactive = await ReadAsync(await owner.GetAsync(Relative($"/api/v1/customers/{customer}/balance")));

        Assert.Equal((0m, 0, "GMD"), (none.GetProperty("totalOutstanding").GetDecimal(), none.GetProperty("openSaleCount").GetInt32(), none.GetProperty("currency").GetString()));
        Assert.Equal(100m, inactive.GetProperty("totalOutstanding").GetDecimal());
        Assert.Equal(sale, inactive.GetProperty("openSales")[0].GetProperty("saleId").GetGuid());
    }

    [Fact]
    public async Task TheBalanceOfAnUnknownCustomer_Is404()
    {
        var (owner, _) = await OwnerAsync();

        var response = await owner.GetAsync(Relative($"/api/v1/customers/{Guid.CreateVersion7()}/balance"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("customer.not_found", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheOpenApiDocument_DocumentsThePaymentEndpointsAndTheBalance()
    {
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync(Relative("/openapi/v1.json"));

        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        var record = paths.GetProperty("/api/v1/sales/{id}/payments").GetProperty("post");
        Assert.Equal("RecordPayment", record.GetProperty("operationId").GetString());
        Assert.True(record.GetProperty("responses").TryGetProperty("422", out _));
        Assert.True(record.GetProperty("responses").TryGetProperty("409", out _));
        Assert.Equal("ReversePayment", paths.GetProperty("/api/v1/sales/{id}/payments/{paymentId}/reverse").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("GetCustomerBalance", paths.GetProperty("/api/v1/customers/{id}/balance").GetProperty("get").GetProperty("operationId").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(s => s.Name).ToArray();
        Assert.Contains("PaymentResultDto", schemas);
        Assert.Contains("CustomerBalanceDto", schemas);
        Assert.DoesNotContain("SalePayment", schemas);
    }
}
