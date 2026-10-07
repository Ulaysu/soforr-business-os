using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Customers;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Sales on account and their payments against real PostgreSQL: stored and read back with every field, the amount paid and the
/// balance derived from the records, the sale's concurrency token stopping two payments recorded together, the append-only
/// guard, and, with raw SQL on purpose so the Domain is bypassed, the constraints the database enforces itself.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CreditSalesPersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 30, 15, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");
    private static readonly Guid Seller = Guid.CreateVersion7();

    private async Task<Guid> NewOrganizationAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Gmd, "UTC", Now));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> NewProductAsync(Guid organization)
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), "Rice", "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), null, Now);
        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<Guid> NewCustomerAsync(Guid organization)
    {
        var customer = Customer.Create(Guid.CreateVersion7(), organization, "Awa Jallow", null, 30, Now);
        await using var db = database.CreateContext(organization);
        db.Set<Customer>().Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>A sale of 100 (10 x 10) on account, due in 30 days, with <paramref name="paidAtSale"/> paid now.</summary>
    private static Sale OnAccount(Guid organization, Guid product, Guid customer, decimal? paidAtSale = null, string? method = "Cash")
    {
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Rice", "bag", Quantity.Create(10m), Money.Create(10m, Gmd), Money.Create(10m, Gmd), Money.Create(5m, Gmd), null);
        return Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Seller, Now, [line], customer, Now.AddDays(30), paidAtSale is { } paid ? Money.Create(paid, Gmd) : null, method);
    }

    private async Task<Sale> SaveAsync(Guid organization, Sale sale)
    {
        await using var db = database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
        return sale;
    }

    private async Task<Sale> LoadAsync(Guid organization, Guid saleId)
    {
        await using var db = database.CreateContext(organization);
        return await db.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).AsNoTracking().SingleAsync(s => s.Id == saleId);
    }

    private async Task<(Guid Organization, Guid Product, Guid Customer)> SeedAsync()
    {
        var organization = await NewOrganizationAsync();
        return (organization, await NewProductAsync(organization), await NewCustomerAsync(organization));
    }

    // ---- the aggregate

    [Fact]
    public async Task ASaleOnAccount_IsStoredAndReadBack_WithItsCustomerDueDateAndPayments()
    {
        var (organization, product, customer) = await SeedAsync();
        var sale = await SaveAsync(organization, OnAccount(organization, product, customer, paidAtSale: 40m, method: "Mobile money"));

        var loaded = await LoadAsync(organization, sale.Id);

        Assert.Equal((customer, Now.AddDays(30), 0), (loaded.CustomerId, loaded.DueAtUtc, loaded.PaymentRevision));
        var payment = Assert.Single(loaded.Payments);
        Assert.Equal((sale.Id, SalePaymentKind.Payment, 40m, "Mobile money", Now, Seller), (payment.Id, payment.Kind, payment.Amount.Amount, payment.MethodLabel, payment.PaidAtUtc, payment.RecordedByUserId));
        Assert.Equal((40m, 60m), (loaded.AmountPaid.Amount, loaded.Outstanding.Amount));
    }

    [Fact]
    public async Task AWalkInSale_IsStoredWithNoCustomerDueDateOrPayment()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Rice", "bag", Quantity.Create(1m), Money.Create(10m, Gmd), Money.Create(10m, Gmd), Money.Create(5m, Gmd), null);
        var sale = await SaveAsync(organization, Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Seller, Now, [line]));

        var loaded = await LoadAsync(organization, sale.Id);

        Assert.Equal((null, null), (loaded.CustomerId, loaded.DueAtUtc));
        Assert.Empty(loaded.Payments);
        Assert.Equal(0m, loaded.Outstanding.Amount);
    }

    [Fact]
    public async Task PaymentsAndReversals_ArePersisted_AndTheBalanceIsDerivedFromThem()
    {
        var (organization, product, customer) = await SeedAsync();
        var sale = await SaveAsync(organization, OnAccount(organization, product, customer, paidAtSale: 10m));
        var second = Guid.CreateVersion7();

        await using (var db = database.CreateContext(organization))
        {
            var loaded = await db.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleAsync(s => s.Id == sale.Id);
            loaded.RecordPayment(second, Money.Create(25m, Gmd), Now.AddDays(1), "Cash", Seller, Now.AddDays(1));
            loaded.RecordPayment(Guid.CreateVersion7(), Money.Create(5m, Gmd), Now.AddDays(2), null, Seller, Now.AddDays(2));
            loaded.ReversePayment(Guid.CreateVersion7(), second, "Wrong customer", Seller, Now.AddDays(3));
            await db.SaveChangesAsync();
        }

        var reloaded = await LoadAsync(organization, sale.Id);
        Assert.Equal(4, reloaded.Payments.Count);
        Assert.Equal(3, reloaded.PaymentRevision);
        var reversal = Assert.Single(reloaded.Payments, p => p.Kind == SalePaymentKind.Reversal);
        Assert.Equal((second, "Wrong customer", 25m), (reversal.ReversesPaymentId, reversal.ReversalReason, reversal.Amount.Amount));
        Assert.Equal(15m, reloaded.AmountPaid.Amount); // 10 + 25 + 5 - 25
        Assert.Equal(85m, reloaded.Outstanding.Amount);
        Assert.Equal(await RawAmountPaidAsync(sale.Id), reloaded.AmountPaid.Amount); // the same figure from the raw records
    }

    private async Task<decimal> RawAmountPaidAsync(Guid saleId) =>
        Convert.ToDecimal(await ScalarAsync("SELECT COALESCE(SUM(CASE WHEN \"Kind\" = 'Payment' THEN \"Amount\" ELSE -\"Amount\" END), 0) FROM \"SalePayments\" WHERE \"SaleId\" = @id", ("id", saleId)), CultureInfo.InvariantCulture);

    // ---- concurrency

    [Fact]
    public async Task TwoPaymentsRecordedTogether_CannotBothSave_SoTheyCannotTogetherPayMoreThanIsOwed()
    {
        var (organization, product, customer) = await SeedAsync();
        var sale = await SaveAsync(organization, OnAccount(organization, product, customer, paidAtSale: 0m));

        await using var one = database.CreateContext(organization);
        await using var two = database.CreateContext(organization);
        var saleOne = await one.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleAsync(s => s.Id == sale.Id);
        var saleTwo = await two.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleAsync(s => s.Id == sale.Id);
        saleOne.RecordPayment(Guid.CreateVersion7(), Money.Create(70m, Gmd), Now.AddDays(1), "Cash", Seller, Now.AddDays(1));
        saleTwo.RecordPayment(Guid.CreateVersion7(), Money.Create(70m, Gmd), Now.AddDays(1), "Cash", Seller, Now.AddDays(1)); // each alone is fine; together 140 > 100

        await one.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => two.SaveChangesAsync());
        Assert.Contains(exception.Entries, entry => entry.Entity is Sale);
        var loaded = await LoadAsync(organization, sale.Id);
        Assert.Equal(70m, loaded.AmountPaid.Amount);
        Assert.Single(loaded.Payments);
    }

    // ---- append-only

    [Fact]
    public async Task APaymentAndASaleLine_CannotBeChangedOrDeleted()
    {
        var (organization, product, customer) = await SeedAsync();
        var sale = await SaveAsync(organization, OnAccount(organization, product, customer, paidAtSale: 40m));

        await using (var db = database.CreateContext(organization))
        {
            var payment = await db.Set<SalePayment>().SingleAsync(p => p.SaleId == sale.Id);
            db.Entry(payment).Property(p => p.MethodLabel).CurrentValue = "Edited";
            await Assert.ThrowsAsync<AppendOnlyViolationException>(() => db.SaveChangesAsync());
        }

        await using (var db = database.CreateContext(organization))
        {
            db.Set<SalePayment>().Remove(await db.Set<SalePayment>().SingleAsync(p => p.SaleId == sale.Id));
            await Assert.ThrowsAsync<AppendOnlyViolationException>(() => db.SaveChangesAsync());
        }

        await using (var db = database.CreateContext(organization))
        {
            var line = await db.Set<SaleLine>().SingleAsync(l => l.SaleId == sale.Id);
            db.Entry(line).Property(l => l.NameSnapshot).CurrentValue = "Edited";
            await Assert.ThrowsAsync<AppendOnlyViolationException>(() => db.SaveChangesAsync());
        }

        var loaded = await LoadAsync(organization, sale.Id);
        Assert.Equal("Cash", Assert.Single(loaded.Payments).MethodLabel);
        Assert.Equal("Rice", Assert.Single(loaded.Lines).NameSnapshot);
    }

    [Fact]
    public async Task ASaleCanStillBeVoided_ThroughTheGuard()
    {
        var (organization, product, customer) = await SeedAsync();
        var sale = await SaveAsync(organization, OnAccount(organization, product, customer, paidAtSale: 0m));

        await using (var db = database.CreateContext(organization))
        {
            var loaded = await db.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleAsync(s => s.Id == sale.Id);
            loaded.Void(Guid.CreateVersion7(), "Cancelled", Now.AddDays(1));
            await db.SaveChangesAsync();
        }

        Assert.Equal(SaleStatus.Voided, (await LoadAsync(organization, sale.Id)).Status);
    }

    // ---- constraints the database enforces itself

    [Fact]
    public async Task TheDatabase_RejectsASaleWhoseCustomerAndDueDateDoNotMatch_OrWhoseDueDateIsBeforeTheSale()
    {
        var (organization, _, customer) = await SeedAsync();

        var customerWithoutDue = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, customer: customer, due: null));
        var dueWithoutCustomer = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, customer: null, due: Now.AddDays(1)));
        var dueBeforeSale = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, customer: customer, due: Now.AddSeconds(-1)));
        var negativeRevision = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, customer: null, due: null, revision: -1));
        await InsertSaleRawAsync(organization, customer: customer, due: Now); // due immediately is fine
        await InsertSaleRawAsync(organization, customer: null, due: null);

        Assert.Equal("CK_Sales_Customer_MatchesDueDate", customerWithoutDue.ConstraintName);
        Assert.Equal("CK_Sales_Customer_MatchesDueDate", dueWithoutCustomer.ConstraintName);
        Assert.Equal("CK_Sales_DueAt_NotBeforeSale", dueBeforeSale.ConstraintName);
        Assert.Equal("CK_Sales_PaymentRevision_NotNegative", negativeRevision.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RefusesASaleOnAccountToAnotherOrganizationsCustomer()
    {
        var (organization, _, _) = await SeedAsync();
        var other = await NewOrganizationAsync();
        var theirCustomer = await NewCustomerAsync(other);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, customer: theirCustomer, due: Now.AddDays(1)));

        Assert.Equal("23503", exception.SqlState);
    }

    private async Task<Guid> SeedSaleOnAccountRawAsync(Guid organization, Guid customer)
    {
        var sale = Guid.CreateVersion7();
        await InsertSaleRawAsync(organization, customer: customer, due: Now.AddDays(30), id: sale);
        return sale;
    }

    [Theory]
    [InlineData("kind", "Refund", "CK_SalePayments_Kind_Known")]
    [InlineData("amount", "0", "CK_SalePayments_Amount_Positive")]
    [InlineData("amount", "-5", "CK_SalePayments_Amount_Positive")]
    [InlineData("currency", "gmd", "CK_SalePayments_AmountCurrency_Format")]
    [InlineData("method", "  ", "CK_SalePayments_MethodLabel_NotBlank")]
    public async Task TheDatabase_RejectsAnInvalidPayment(string field, string value, string constraint)
    {
        var (organization, _, customer) = await SeedAsync();
        var sale = await SeedSaleOnAccountRawAsync(organization, customer);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, sale, (field, value)));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsAPaymentDatedAfterItWasRecorded()
    {
        var (organization, _, customer) = await SeedAsync();
        var sale = await SeedSaleOnAccountRawAsync(organization, customer);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, sale, ("paidAt", Now.AddDays(2).ToString("O")), ("recordedAt", Now.AddDays(1).ToString("O"))));

        Assert.Equal("CK_SalePayments_PaidAt_NotAfterRecorded", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsAReversalThatDoesNotMatchItsKind()
    {
        var (organization, _, customer) = await SeedAsync();
        var sale = await SeedSaleOnAccountRawAsync(organization, customer);
        var payment = Guid.CreateVersion7();
        await InsertPaymentRawAsync(organization, sale, ("id", payment.ToString()));

        var reversalWithoutTarget = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, sale, ("kind", "Reversal"), ("reason", "Why")));
        var reversalWithoutReason = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, sale, ("kind", "Reversal"), ("reverses", payment.ToString())));
        var paymentWithTarget = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, sale, ("reverses", payment.ToString()), ("reason", "Why")));
        var blankReason = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, sale, ("kind", "Reversal"), ("reverses", payment.ToString()), ("reason", "  ")));

        Assert.Equal("CK_SalePayments_Reversal_MatchesKind", reversalWithoutTarget.ConstraintName);
        Assert.Equal("CK_SalePayments_Reversal_MatchesKind", reversalWithoutReason.ConstraintName);
        Assert.Equal("CK_SalePayments_Reversal_MatchesKind", paymentWithTarget.ConstraintName);
        Assert.Equal("CK_SalePayments_ReversalReason_NotBlank", blankReason.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_AllowsAPaymentToBeReversedOnce_ButNotTwice_AndNotFromAnotherSale()
    {
        var (organization, _, customer) = await SeedAsync();
        var sale = await SeedSaleOnAccountRawAsync(organization, customer);
        var otherSale = await SeedSaleOnAccountRawAsync(organization, customer);
        var payment = Guid.CreateVersion7();
        await InsertPaymentRawAsync(organization, sale, ("id", payment.ToString()));

        var untouched = Guid.CreateVersion7();
        await InsertPaymentRawAsync(organization, sale, ("id", untouched.ToString()));

        await InsertPaymentRawAsync(organization, sale, ("kind", "Reversal"), ("reverses", payment.ToString()), ("reason", "Wrong"));
        var twice = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, sale, ("kind", "Reversal"), ("reverses", payment.ToString()), ("reason", "Again")));

        // A payment that was never reversed, but belongs to another sale, cannot be reversed from this one.
        var fromAnotherSale = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, otherSale, ("kind", "Reversal"), ("reverses", untouched.ToString()), ("reason", "Not mine")));

        Assert.Equal("23505", twice.SqlState);
        Assert.Equal("23503", fromAnotherSale.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RefusesPaymentsThatMixOrganizations_AndAUnknownSale()
    {
        var (organization, _, customer) = await SeedAsync();
        var sale = await SeedSaleOnAccountRawAsync(organization, customer);
        var other = await NewOrganizationAsync();

        var inForeignOrganization = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(other, sale));
        var unknownSale = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, Guid.CreateVersion7()));

        Assert.All([inForeignOrganization, unknownSale], exception => Assert.Equal("23503", exception.SqlState));
    }

    [Fact]
    public async Task ThePaymentKey_IsPerOrganization_SoAnotherOrganizationMayUseTheSameId()
    {
        var (organization, _, customer) = await SeedAsync();
        var (other, _, otherCustomer) = await SeedAsync();
        var sale = await SeedSaleOnAccountRawAsync(organization, customer);
        var otherSale = await SeedSaleOnAccountRawAsync(other, otherCustomer);
        var shared = Guid.CreateVersion7();

        await InsertPaymentRawAsync(organization, sale, ("id", shared.ToString()));
        await InsertPaymentRawAsync(other, otherSale, ("id", shared.ToString()));
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertPaymentRawAsync(organization, sale, ("id", shared.ToString())));

        Assert.Equal("23505", duplicate.SqlState);
    }

    [Fact]
    public async Task ASalePaymentTable_HasTheExpectedColumns()
    {
        var columns = new Dictionary<string, (string Type, string Nullable)>();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name = 'SalePayments'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
            }
        }

        Assert.Equal(("character varying", "NO"), columns["Kind"]);
        Assert.Equal(("numeric", "NO"), columns["Amount"]);
        Assert.Equal(("character varying", "YES"), columns["MethodLabel"]);
        Assert.Equal(("uuid", "YES"), columns["ReversesPaymentId"]);
        Assert.Equal(("character varying", "YES"), columns["ReversalReason"]);
        Assert.Equal(12, columns.Count);
    }

    // ---- raw helpers

    private async Task InsertSaleRawAsync(Guid organization, Guid? customer, DateTimeOffset? due, int revision = 0, Guid? id = null) =>
        await ExecuteAsync(
            "INSERT INTO \"Sales\" (\"Id\", \"OrganizationId\", \"Currency\", \"Status\", \"CreatedByUserId\", \"CreatedAtUtc\", \"CustomerId\", \"DueAtUtc\", \"PaymentRevision\") VALUES (@id, @org, 'GMD', 'Completed', @user, @now, @customer, @due, @revision)",
            ("id", id ?? Guid.CreateVersion7()),
            ("org", organization),
            ("user", Seller),
            ("now", Now),
            ("customer", (object?)customer ?? DBNull.Value),
            ("due", (object?)due ?? DBNull.Value),
            ("revision", revision));

    private async Task InsertPaymentRawAsync(Guid organization, Guid sale, params (string Field, string Value)[] overrides)
    {
        var values = new Dictionary<string, string>
        {
            ["id"] = Guid.CreateVersion7().ToString(),
            ["kind"] = "Payment",
            ["amount"] = "10",
            ["currency"] = "GMD",
            ["method"] = "Cash",
            ["reverses"] = string.Empty,
            ["reason"] = string.Empty,
            ["paidAt"] = Now.AddMinutes(1).ToString("O"),
            ["recordedAt"] = Now.AddMinutes(1).ToString("O"),
        };
        foreach (var (field, value) in overrides)
        {
            values[field] = value;
        }

        await ExecuteAsync(
            "INSERT INTO \"SalePayments\" (\"Id\", \"OrganizationId\", \"SaleId\", \"Kind\", \"Amount\", \"AmountCurrency\", \"PaidAtUtc\", \"MethodLabel\", \"ReversesPaymentId\", \"ReversalReason\", \"RecordedByUserId\", \"RecordedAtUtc\") " +
            "VALUES (@id, @org, @sale, @kind, @amount, @currency, @paidAt, @method, @reverses, @reason, @user, @recordedAt)",
            ("id", Guid.Parse(values["id"])),
            ("org", organization),
            ("sale", sale),
            ("kind", values["kind"]),
            ("amount", decimal.Parse(values["amount"], CultureInfo.InvariantCulture)),
            ("currency", values["currency"]),
            ("paidAt", DateTimeOffset.Parse(values["paidAt"], CultureInfo.InvariantCulture)),
            ("method", values["method"]),
            ("reverses", values["reverses"].Length == 0 ? DBNull.Value : Guid.Parse(values["reverses"])),
            ("reason", values["reason"].Length == 0 ? DBNull.Value : values["reason"]),
            ("user", Seller),
            ("recordedAt", DateTimeOffset.Parse(values["recordedAt"], CultureInfo.InvariantCulture)));
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync();
    }
}
