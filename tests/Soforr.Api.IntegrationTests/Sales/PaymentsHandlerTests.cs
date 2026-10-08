using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Soforr.Api.IntegrationTests.Offline;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Offline;
using Soforr.Application.Organizations;
using Soforr.Application.Sales;
using Soforr.Application.Sales.GetCustomerBalance;
using Soforr.Application.Sales.RecordPayment;
using Soforr.Application.Sales.ReversePayment;
using Soforr.Domain.Audit;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Customers;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Sales;

/// <summary>
/// Recording and reversing payments, and the customer balance, on real PostgreSQL with a fixed clock (2026-06-01 10:00 UTC) and the
/// races forced deterministically: a competing writer that commits between this request's read and its save, a save that
/// fails, the overdue boundary to the second, and the balance reconciled against a raw SQL sum of the stored records.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PaymentsHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SoldAt = Now.AddDays(-5);
    private static readonly Currency Gmd = Currency.Create("GMD");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    private sealed class NewIds : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser, IOrganizationContext
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;

        public static FakeUser In(Guid organizationId, Role role) => new(Guid.CreateVersion7(), organizationId, role);
    }

    private sealed class UnitOfWorkAuditLog(AppDbContext db) : IAuditLog
    {
        public void Record(AuditEntry entry) =>
            db.Set<AuditEvent>().Add(AuditEvent.Record(Guid.CreateVersion7(), entry.OrganizationId, entry.ActorId, entry.Action, entry.EntityType, entry.EntityId, entry.Description, "test-corr", Now));
    }

    /// <summary>Runs <paramref name="action"/> once, just before the first save of the request under test.</summary>
    private sealed class OnceBeforeSaveInterceptor(Func<Task> action) : SaveChangesInterceptor
    {
        private int _ran;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _ran, 1) == 0)
            {
                await action();
            }

            return result;
        }
    }

    private async Task<Guid> NewOrganizationAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Gmd, "UTC", SoldAt.AddDays(-30)));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> NewCustomerAsync(Guid organization, int? terms = 30, string name = "Awa Jallow")
    {
        var customer = Customer.Create(Guid.CreateVersion7(), organization, name, null, terms, SoldAt.AddDays(-10));
        await using var db = database.CreateContext(organization);
        db.Set<Customer>().Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private async Task<Guid> NewProductAsync(Guid organization)
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), "Rice", "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), null, SoldAt.AddDays(-10));
        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>Stores a completed sale on account (quantity x price), sold at <paramref name="soldAt"/> and due at <paramref name="dueAt"/>.</summary>
    private async Task<Guid> NewSaleAsync(Guid organization, Guid customer, Guid product, decimal quantity = 10m, decimal price = 10m, decimal paidAtSale = 0m, DateTimeOffset? soldAt = null, DateTimeOffset? dueAt = null)
    {
        var sold = soldAt ?? SoldAt;
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Rice", "bag", Quantity.Create(quantity), Money.Create(price, Gmd), Money.Create(price, Gmd), Money.Create(5m, Gmd), null);
        var sale = Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Guid.CreateVersion7(), sold, [line], customer, dueAt ?? sold.AddDays(30), Money.Create(paidAtSale, Gmd), "Cash");
        await using var db = database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
        return sale.Id;
    }

    private static AppDbContext NewContext(string connectionString, FakeUser user, SaveChangesInterceptor? interceptor)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new AppDbContext(options.Options, user);
    }

    private async Task<Result<PaymentResultDto>> PayAsync(Guid organization, Guid sale, decimal amount, Guid? paymentId = null, SaveChangesInterceptor? interceptor = null, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = NewContext(database.ConnectionString, user, interceptor);
        return await new RecordPaymentHandler(db, new FixedClock(), new NewIds(), user, new UnitOfWorkAuditLog(db), new OfflineEventTimeValidator(db, new FixedClock(), user, TestOfflineSettings.Default, new UnitOfWorkAuditLog(db)))
            .HandleAsync(new RecordPaymentCommand(sale, paymentId, amount, null, "Cash"), CancellationToken.None);
    }

    private async Task<Result<PaymentResultDto>> ReverseAsync(Guid organization, Guid sale, Guid payment, string reason = "Wrong", Guid? reversalId = null, SaveChangesInterceptor? interceptor = null)
    {
        var user = FakeUser.In(organization, Role.Owner);
        await using var db = NewContext(database.ConnectionString, user, interceptor);
        return await new ReversePaymentHandler(db, new FixedClock(), new NewIds(), user, new UnitOfWorkAuditLog(db))
            .HandleAsync(new ReversePaymentCommand(sale, payment, reversalId, reason), CancellationToken.None);
    }

    private async Task<Result<CustomerBalanceDto>> BalanceAsync(Guid organization, Guid customer, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = NewContext(database.ConnectionString, user, interceptor: null);
        return await new GetCustomerBalanceHandler(db, new FixedClock(), user).HandleAsync(new GetCustomerBalanceQuery(customer), CancellationToken.None);
    }

    private async Task<(int Payments, int Reversals, decimal Paid, int Audits)> StateAsync(Guid organization, Guid sale)
    {
        await using var db = database.CreateContext(organization);
        var records = await db.Set<SalePayment>().Where(p => p.SaleId == sale).ToListAsync();
        return (
            records.Count(p => p.Kind == SalePaymentKind.Payment),
            records.Count(p => p.Kind == SalePaymentKind.Reversal),
            records.Sum(p => p.SignedAmount),
            await db.Set<AuditEvent>().CountAsync(e => e.EntityId == sale && (e.Action == AuditActions.PaymentRecorded || e.Action == AuditActions.PaymentReversed)));
    }

    private async Task<(Guid Organization, Guid Customer, Guid Sale)> SeedSaleAsync(decimal paidAtSale = 0m)
    {
        var organization = await NewOrganizationAsync();
        var customer = await NewCustomerAsync(organization);
        var sale = await NewSaleAsync(organization, customer, await NewProductAsync(organization), paidAtSale: paidAtSale);
        return (organization, customer, sale);
    }

    // ---- races on recording a payment

    [Fact]
    public async Task ACompetingIdenticalPaymentCommittingFirst_IsRecognized_SoTheMoneyIsRecordedOnce()
    {
        var (organization, _, sale) = await SeedSaleAsync();
        var id = Guid.CreateVersion7();
        var competitor = new OnceBeforeSaveInterceptor(async () => Assert.True((await PayAsync(organization, sale, 40m, id)).IsSuccess));

        var result = await PayAsync(organization, sale, 40m, id, competitor);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.True(result.Value.AlreadyRecorded);
        Assert.Equal(60m, result.Value.Sale.Outstanding);
        Assert.Equal((1, 0, 40m, 1), await StateAsync(organization, sale));
    }

    [Fact]
    public async Task ACompetingPaymentWithTheSameIdAndAnotherAmount_WinsOnce_AndOursIsAConflict()
    {
        var (organization, _, sale) = await SeedSaleAsync();
        var id = Guid.CreateVersion7();
        var competitor = new OnceBeforeSaveInterceptor(async () => Assert.True((await PayAsync(organization, sale, 40m, id)).IsSuccess));

        var result = await PayAsync(organization, sale, 55m, id, competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("sale.payment_id_conflict", result.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Equal((1, 0, 40m, 1), await StateAsync(organization, sale));
    }

    [Fact]
    public async Task ACompetingPaymentThatUsesUpTheBalance_MeansOursIsRetriedAndRefused_NotRecordedOnTop()
    {
        var (organization, _, sale) = await SeedSaleAsync();
        var competitor = new OnceBeforeSaveInterceptor(async () => Assert.True((await PayAsync(organization, sale, 80m)).IsSuccess));

        // 50 fits the 100 owed when the request read the sale, but not the 20 left once the competitor paid 80.
        var result = await PayAsync(organization, sale, 50m, interceptor: competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("sale.payment_exceeds_outstanding", result.Error!.Code);
        Assert.Equal(ErrorKind.RuleViolation, result.Error.Kind);
        Assert.Equal((1, 0, 80m, 1), await StateAsync(organization, sale));
    }

    [Fact]
    public async Task ACompetingPaymentThatLeavesRoom_IsRetried_AndBothAreRecorded()
    {
        var (organization, _, sale) = await SeedSaleAsync();
        var competitor = new OnceBeforeSaveInterceptor(async () => Assert.True((await PayAsync(organization, sale, 30m)).IsSuccess));

        var result = await PayAsync(organization, sale, 50m, interceptor: competitor);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value.AlreadyRecorded);
        Assert.Equal(20m, result.Value.Sale.Outstanding);
        Assert.Equal((2, 0, 80m, 2), await StateAsync(organization, sale));
    }

    [Fact]
    public async Task APaymentThatFailsToSave_LeavesNothing_AndDoesNotReserveItsId()
    {
        var (organization, _, sale) = await SeedSaleAsync();
        var id = Guid.CreateVersion7();
        var user = FakeUser.In(organization, Role.Owner);
        await using (var db = NewContext(database.ConnectionString, user, new OnceBeforeSaveInterceptor(() => throw new InvalidOperationException("simulated failure before the save"))))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RecordPaymentHandler(db, new FixedClock(), new NewIds(), user, new UnitOfWorkAuditLog(db), new OfflineEventTimeValidator(db, new FixedClock(), user, TestOfflineSettings.Default, new UnitOfWorkAuditLog(db))).HandleAsync(new RecordPaymentCommand(sale, id, 40m, null, "Cash"), CancellationToken.None));
        }

        Assert.Equal((0, 0, 0m, 0), await StateAsync(organization, sale));

        var result = await PayAsync(organization, sale, 25m, id); // even another amount is fine: nothing committed under this id

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal((1, 0, 25m, 1), await StateAsync(organization, sale));
    }

    [Fact]
    public async Task APaymentIdOfAnotherOrganization_IsNotSeen()
    {
        var (first, _, firstSale) = await SeedSaleAsync();
        var (second, _, secondSale) = await SeedSaleAsync();
        var id = Guid.CreateVersion7();
        Assert.True((await PayAsync(first, firstSale, 20m, id)).IsSuccess);

        var result = await PayAsync(second, secondSale, 30m, id);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value.AlreadyRecorded);
        Assert.Equal((1, 0, 30m, 1), await StateAsync(second, secondSale));
    }

    // ---- races on reversing a payment

    [Fact]
    public async Task ACompetingReversalOfTheSamePayment_MeansOursIsRefused_AndTheMoneyIsReversedOnce()
    {
        var (organization, _, sale) = await SeedSaleAsync();
        var payment = (await PayAsync(organization, sale, 60m)).Value.PaymentId;
        var competitor = new OnceBeforeSaveInterceptor(async () => Assert.True((await ReverseAsync(organization, sale, payment, "First")).IsSuccess));

        var result = await ReverseAsync(organization, sale, payment, "Second", interceptor: competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("sale.payment_already_reversed", result.Error!.Code);
        Assert.Equal(ErrorKind.RuleViolation, result.Error.Kind);
        Assert.Equal((1, 1, 0m, 2), await StateAsync(organization, sale));
    }

    [Fact]
    public async Task ACompetingIdenticalReversalCommittingFirst_IsRecognizedAsARepeat()
    {
        var (organization, _, sale) = await SeedSaleAsync();
        var payment = (await PayAsync(organization, sale, 60m)).Value.PaymentId;
        var reversalId = Guid.CreateVersion7();
        var competitor = new OnceBeforeSaveInterceptor(async () => Assert.True((await ReverseAsync(organization, sale, payment, "Wrong", reversalId)).IsSuccess));

        var result = await ReverseAsync(organization, sale, payment, "Wrong", reversalId, competitor);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.True(result.Value.AlreadyRecorded);
        Assert.Equal((1, 1, 0m, 2), await StateAsync(organization, sale));
    }

    [Fact]
    public async Task APaymentRecordedWhileAnotherIsReversed_IsRetried_AndBothHold()
    {
        var (organization, _, sale) = await SeedSaleAsync();
        var payment = (await PayAsync(organization, sale, 60m)).Value.PaymentId;
        var competitor = new OnceBeforeSaveInterceptor(async () => Assert.True((await PayAsync(organization, sale, 10m)).IsSuccess));

        var result = await ReverseAsync(organization, sale, payment, "Wrong", interceptor: competitor);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal((2, 1, 10m, 3), await StateAsync(organization, sale));
        Assert.Equal(90m, result.Value.Sale.Outstanding);
    }

    // ---- the balance

    [Fact]
    public async Task ASaleIsOverdue_OnlyWhenItsDueDateIsStrictlyBeforeNow()
    {
        var organization = await NewOrganizationAsync();
        var customer = await NewCustomerAsync(organization);
        var product = await NewProductAsync(organization);
        var justPassed = await NewSaleAsync(organization, customer, product, dueAt: Now.AddSeconds(-1));
        var exactlyNow = await NewSaleAsync(organization, customer, product, dueAt: Now);
        var justAhead = await NewSaleAsync(organization, customer, product, dueAt: Now.AddSeconds(1));

        var balance = (await BalanceAsync(organization, customer)).Value;

        Assert.Equal(Now, balance.AsOfUtc);
        Assert.Equal(300m, balance.TotalOutstanding);
        Assert.Equal((100m, 1, 3), (balance.OverdueAmount, balance.OverdueSaleCount, balance.OpenSaleCount));
        Assert.True(balance.OpenSales.Single(s => s.SaleId == justPassed).IsOverdue);
        Assert.False(balance.OpenSales.Single(s => s.SaleId == exactlyNow).IsOverdue);
        Assert.False(balance.OpenSales.Single(s => s.SaleId == justAhead).IsOverdue);
        Assert.Equal([justPassed, exactlyNow, justAhead], balance.OpenSales.Select(s => s.SaleId).ToArray());
    }

    [Fact]
    public async Task OnlyThePartOfTheSaleStillOwed_IsOverdue()
    {
        var organization = await NewOrganizationAsync();
        var customer = await NewCustomerAsync(organization);
        var sale = await NewSaleAsync(organization, customer, await NewProductAsync(organization), paidAtSale: 30m, dueAt: Now.AddDays(-2));
        Assert.True((await PayAsync(organization, sale, 20m)).IsSuccess);

        var balance = (await BalanceAsync(organization, customer)).Value;

        Assert.Equal((50m, 50m), (balance.TotalOutstanding, balance.OverdueAmount));
        var open = Assert.Single(balance.OpenSales);
        Assert.Equal((100m, 50m, 50m), (open.Total, open.AmountPaid, open.Outstanding));
    }

    [Fact]
    public async Task OpenSales_AreOrderedByDueDate_ThenSaleDate_ThenId()
    {
        var organization = await NewOrganizationAsync();
        var customer = await NewCustomerAsync(organization);
        var product = await NewProductAsync(organization);
        var late = await NewSaleAsync(organization, customer, product, dueAt: Now.AddDays(10));
        var earlyButSoldLater = await NewSaleAsync(organization, customer, product, soldAt: Now.AddDays(-1), dueAt: Now.AddDays(2));
        var earlyAndSoldEarlier = await NewSaleAsync(organization, customer, product, soldAt: Now.AddDays(-3), dueAt: Now.AddDays(2));

        var balance = (await BalanceAsync(organization, customer)).Value;

        Assert.Equal([earlyAndSoldEarlier, earlyButSoldLater, late], balance.OpenSales.Select(s => s.SaleId).ToArray());
    }

    [Fact]
    public async Task TheBalance_ReconcilesWithTheStoredRecords_ToTheLastDecimal()
    {
        var organization = await NewOrganizationAsync();
        var customer = await NewCustomerAsync(organization);
        var other = await NewCustomerAsync(organization, name: "Someone Else");
        var product = await NewProductAsync(organization);
        // Fractional quantities and prices make every line total need rounding.
        var first = await NewSaleAsync(organization, customer, product, quantity: 0.3333m, price: 1.5m, soldAt: Now.AddDays(-9));
        var second = await NewSaleAsync(organization, customer, product, quantity: 7.7777m, price: 3.3333m, paidAtSale: 10m, soldAt: Now.AddDays(-8));
        var third = await NewSaleAsync(organization, customer, product, quantity: 2.5m, price: 9.9999m, soldAt: Now.AddDays(-7));
        var settled = await NewSaleAsync(organization, customer, product, quantity: 1m, price: 5m, paidAtSale: 5m, soldAt: Now.AddDays(-6));
        await NewSaleAsync(organization, other, product, quantity: 100m, price: 100m); // another customer's
        Assert.True((await PayAsync(organization, third, 12.3456m)).IsSuccess);
        var toReverse = (await PayAsync(organization, third, 5m)).Value.PaymentId;
        Assert.True((await ReverseAsync(organization, third, toReverse)).IsSuccess);
        var voidedSale = await NewSaleAsync(organization, customer, product, quantity: 50m, price: 50m, soldAt: Now.AddDays(-5));
        await using (var db = database.CreateContext(organization))
        {
            var loaded = await db.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleAsync(s => s.Id == voidedSale);
            loaded.Void(Guid.CreateVersion7(), "Cancelled", Now.AddDays(-4));
            await db.SaveChangesAsync();
        }

        var balance = (await BalanceAsync(organization, customer)).Value;

        var raw = await RawOutstandingAsync(organization, customer);
        Assert.Equal(raw, balance.TotalOutstanding);
        Assert.NotEqual(0m, raw);
        Assert.Equal(balance.TotalOutstanding, balance.OpenSales.Sum(s => s.Outstanding));
        Assert.Equal(new[] { first, second, third }.Order(), balance.OpenSales.Select(s => s.SaleId).Order());
        Assert.DoesNotContain(balance.OpenSales, s => s.SaleId == settled || s.SaleId == voidedSale);
    }

    private async Task<decimal> RawOutstandingAsync(Guid organization, Guid customer)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(SUM(t.total - COALESCE(p.paid, 0)), 0) FROM " +
            "(SELECT s.\"Id\" AS id, SUM(ROUND(l.\"UnitPrice\" * l.\"Quantity\", 4)) AS total FROM \"Sales\" s JOIN \"SaleLines\" l ON l.\"SaleId\" = s.\"Id\" AND l.\"OrganizationId\" = s.\"OrganizationId\" " +
            " WHERE s.\"OrganizationId\" = @org AND s.\"CustomerId\" = @customer AND s.\"Status\" = 'Completed' GROUP BY s.\"Id\") t " +
            "LEFT JOIN (SELECT \"SaleId\", SUM(CASE WHEN \"Kind\" = 'Payment' THEN \"Amount\" ELSE -\"Amount\" END) AS paid FROM \"SalePayments\" WHERE \"OrganizationId\" = @org GROUP BY \"SaleId\") p ON p.\"SaleId\" = t.id";
        command.Parameters.AddWithValue("org", organization);
        command.Parameters.AddWithValue("customer", customer);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task AnotherOrganizationsSalesAreNeverInABalance_AndTheirCustomerIsNotFound()
    {
        var (first, firstCustomer, _) = await SeedSaleAsync();
        var (second, _, _) = await SeedSaleAsync();

        var own = await BalanceAsync(first, firstCustomer);
        var foreign = await BalanceAsync(second, firstCustomer);

        Assert.Equal(100m, own.Value.TotalOutstanding);
        Assert.True(foreign.IsFailure);
        Assert.Equal("customer.not_found", foreign.Error!.Code);
    }

    [Fact]
    public async Task ACustomerWhoOwesNothing_HasAnEmptyBalance_InTheOrganizationsCurrency()
    {
        var organization = await NewOrganizationAsync();
        var customer = await NewCustomerAsync(organization);

        var balance = (await BalanceAsync(organization, customer)).Value;

        Assert.Equal((0m, 0m, 0, "GMD"), (balance.TotalOutstanding, balance.OverdueAmount, balance.OpenSaleCount, balance.Currency));
        Assert.Empty(balance.OpenSales);
    }
}
