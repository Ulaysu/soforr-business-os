using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.GetInsights;
using Soforr.Application.Intelligence.GetReceivables;
using Soforr.Application.Intelligence.Sources;
using Soforr.Application.Organizations;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Customers;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Intelligence;

/// <summary>
/// The receivables report and the overdue balance insights on real PostgreSQL, with the clock fixed at 2026-06-01 10:00 UTC. The
/// balances are reconciled against a raw SQL sum of the lines and payments stored for the same sales.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReceivablesTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");
    private static readonly Guid Seller = Guid.CreateVersion7();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser, IOrganizationContext
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;

        public static FakeUser In(Guid organizationId, Role role) => new(Guid.CreateVersion7(), organizationId, role);
    }

    private async Task<Guid> NewOrganizationAsync(string timeZone = "UTC", decimal? threshold = null)
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        var organization = Organization.Create(id, "Soforr Trading", Gmd, timeZone, LongAgo);
        if (threshold is not null)
        {
            organization.UpdateSettings("Soforr Trading", timeZone, threshold, LongAgo);
        }

        db.Set<Organization>().Add(organization);
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> NewCustomerAsync(Guid organization, string name)
    {
        var customer = Customer.Create(Guid.CreateVersion7(), organization, name, null, 30, LongAgo);
        await using var db = database.CreateContext(organization);
        db.Set<Customer>().Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private async Task<Guid> NewProductAsync(Guid organization)
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), "Rice", "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), null, LongAgo);
        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>A completed sale on account of <paramref name="total"/> (one line, price 1 per unit) due at <paramref name="due"/>, with the given amount already paid.</summary>
    private async Task<Guid> CreditSaleAsync(Guid organization, Guid product, Guid? customer, decimal total, DateTimeOffset due, decimal paid = 0m, bool reversePayment = false)
    {
        var line = new SaleLineInput(Guid.CreateVersion7(), product, "SKU", "Rice", "bag", Quantity.Create(total), Money.Create(1m, Gmd), Money.Create(1m, Gmd), Money.Create(0.5m, Gmd), null);
        var soldAt = due < Now.AddDays(-100) ? due.AddDays(-30) : Now.AddDays(-100);
        var sale = customer is null
            ? Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Seller, soldAt, [line])
            : Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Seller, soldAt, [line], customer, due);
        if (paid > 0m)
        {
            var payment = sale.RecordPayment(Guid.CreateVersion7(), Money.Create(paid, Gmd), soldAt.AddDays(1), "Cash", Seller, soldAt.AddDays(1));
            if (reversePayment)
            {
                sale.ReversePayment(Guid.CreateVersion7(), payment.Id, "Bounced", Seller, soldAt.AddDays(2));
            }
        }

        await using var db = database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
        return sale.Id;
    }

    private async Task<Result<ReceivablesDto>> ReportAsync(Guid organization, int? top = null, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        return await new GetReceivablesHandler(db, new FixedClock(), user).HandleAsync(new GetReceivablesQuery(top), CancellationToken.None);
    }

    private async Task<Result<InsightsDto>> InsightsAsync(Guid organization, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        return await new GetInsightsHandler([new ReceivablesInsightSource(db)], new FixedClock(), user).HandleAsync(new GetInsightsQuery(), CancellationToken.None);
    }

    // ---- the report

    [Fact]
    public async Task TheReport_AgesWhatEachCustomerOwes_AndFollowsPaymentsAndReversals()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var awa = await NewCustomerAsync(organization, "Awa Jallow");
        var bakary = await NewCustomerAsync(organization, "Bakary Ceesay");
        await CreditSaleAsync(organization, product, awa, 100m, Now.AddDays(-10), paid: 40m);                          // 60 owed, 10 days past due
        await CreditSaleAsync(organization, product, awa, 50m, Now.AddDays(-100));                                     // 50 owed, 100 days past due
        await CreditSaleAsync(organization, product, awa, 30m, Now.AddDays(5));                                        // 30 not yet due
        await CreditSaleAsync(organization, product, awa, 20m, Now.AddDays(-20), paid: 20m);                           // settled
        await CreditSaleAsync(organization, product, bakary, 70m, Now.AddDays(-45), paid: 25m, reversePayment: true); // payment reversed: 70 owed

        var result = await ReportAsync(organization);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var report = result.Value;
        Assert.Equal(("GMD", "UTC", Now), (report.Currency, report.TimeZoneId, report.AsOfUtc));
        Assert.Equal((210m, 30m, 180m), (report.TotalOutstanding, report.TotalNotYetDue, report.TotalOverdue));
        Assert.Equal(new AgingBucketsDto(60m, 70m, 0m, 50m), report.Buckets);
        Assert.Equal((2, 2), (report.CustomersWithBalance, report.CustomersOverdue));
        Assert.Equal(["Awa Jallow", "Bakary Ceesay"], report.Customers.Select(c => c.Name).ToArray());
        var first = report.Customers[0];
        Assert.Equal((140m, 30m, 110m, 3, 2, 100), (first.Outstanding, first.NotYetDue, first.Overdue, first.OpenSaleCount, first.OverdueSaleCount, first.OldestDaysPastDue));
        Assert.Equal([("91+", 50m), ("0-30", 60m)], first.OverdueSales.Select(s => (s.Bucket, s.Outstanding)).ToArray());
    }

    [Fact]
    public async Task TheReport_ReconcilesWithTheStoredLinesAndPayments()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var awa = await NewCustomerAsync(organization, "Awa Jallow");
        await CreditSaleAsync(organization, product, awa, 100m, Now.AddDays(-10), paid: 40m);
        await CreditSaleAsync(organization, product, awa, 50m, Now.AddDays(-100), paid: 10m, reversePayment: true);
        await CreditSaleAsync(organization, product, awa, 30m, Now.AddDays(5), paid: 5m);

        var report = (await ReportAsync(organization)).Value;

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COALESCE(SUM(l."Quantity" * l."UnitPrice"), 0) FROM "SaleLines" l JOIN "Sales" s ON s."Id" = l."SaleId" AND s."OrganizationId" = l."OrganizationId"
                 WHERE s."OrganizationId" = @org AND s."CustomerId" IS NOT NULL AND s."Status" = 'Completed')
              -
              (SELECT COALESCE(SUM(CASE WHEN p."Kind" = 'Payment' THEN p."Amount" ELSE -p."Amount" END), 0) FROM "SalePayments" p WHERE p."OrganizationId" = @org)
            """;
        command.Parameters.AddWithValue("org", organization);
        var stored = Convert.ToDecimal(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        Assert.Equal(stored, report.TotalOutstanding);
        Assert.Equal(report.TotalOutstanding, report.TotalNotYetDue + report.TotalOverdue);
        Assert.Equal(report.TotalOverdue, report.Buckets.Days0To30 + report.Buckets.Days31To60 + report.Buckets.Days61To90 + report.Buckets.Days91Plus);
    }

    [Fact]
    public async Task WalkInSalesAndVoidedSales_AreNeverReceivables()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var awa = await NewCustomerAsync(organization, "Awa Jallow");
        await CreditSaleAsync(organization, product, null, 500m, Now.AddDays(-10));
        var voided = await CreditSaleAsync(organization, product, awa, 80m, Now.AddDays(-10));
        await using (var db = database.CreateContext(organization))
        {
            var sale = await db.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleAsync(s => s.Id == voided);
            sale.Void(Seller, "Mistake", Now.AddDays(-9));
            await db.SaveChangesAsync();
        }

        var report = (await ReportAsync(organization)).Value;

        Assert.Empty(report.Customers);
        Assert.Equal(0m, report.TotalOutstanding);
    }

    [Fact]
    public async Task TheDaysPastDue_AreCountedInTheOrganizationsTimeZone()
    {
        // Now is 10:00 UTC on June 1 = 19:00 in Tokyo. Due 2026-05-01 16:00 UTC = 01:00 on May 2 in Tokyo.
        var due = new DateTimeOffset(2026, 5, 1, 16, 0, 0, TimeSpan.Zero);
        var utcOrg = await NewOrganizationAsync("UTC");
        var tokyoOrg = await NewOrganizationAsync("Asia/Tokyo");
        var days = new List<int?>();
        foreach (var organization in new[] { utcOrg, tokyoOrg })
        {
            var product = await NewProductAsync(organization);
            var customer = await NewCustomerAsync(organization, "Awa Jallow");
            await CreditSaleAsync(organization, product, customer, 10m, due);
            days.Add((await ReportAsync(organization)).Value.Customers.Single().OldestDaysPastDue);
        }

        Assert.Equal([31, 30], days);
    }

    [Fact]
    public async Task TheTop_LimitsTheListedCustomers_NotTheTotals()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        foreach (var (name, owed) in new[] { ("A", 10m), ("B", 20m), ("C", 30m) })
        {
            await CreditSaleAsync(organization, product, await NewCustomerAsync(organization, name), owed, Now.AddDays(-5));
        }

        var report = (await ReportAsync(organization, top: 2)).Value;

        Assert.Equal(["C", "B"], report.Customers.Select(c => c.Name).ToArray());
        Assert.Equal((60m, 3), (report.TotalOverdue, report.CustomersWithBalance));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ATopOutsideTheRange_IsRefused(int top)
    {
        var organization = await NewOrganizationAsync();

        var result = await ReportAsync(organization, top);

        Assert.Equal(("receivables.top_invalid", "top", ErrorKind.Validation), (result.Error!.Code, result.Error.Field, result.Error.Kind));
    }

    [Fact]
    public async Task AnotherOrganizationsBalances_AreNeverIncluded()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        await CreditSaleAsync(mine, await NewProductAsync(mine), await NewCustomerAsync(mine, "Mine"), 10m, Now.AddDays(-5));
        await CreditSaleAsync(theirs, await NewProductAsync(theirs), await NewCustomerAsync(theirs, "Theirs"), 999m, Now.AddDays(-5));

        var report = (await ReportAsync(mine)).Value;
        var insights = (await InsightsAsync(mine)).Value;

        Assert.Equal(["Mine"], report.Customers.Select(c => c.Name).ToArray());
        Assert.Equal(10m, report.TotalOverdue);
        Assert.Equal(["Mine"], insights.Items.Select(i => i.Subject.Name).ToArray());
    }

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Manager, true)]
    [InlineData(Role.Staff, false)]
    public async Task OnlyThoseWithInsightsView_MayReadTheReportAndTheInsights(Role role, bool allowed)
    {
        var organization = await NewOrganizationAsync();
        await CreditSaleAsync(organization, await NewProductAsync(organization), await NewCustomerAsync(organization, "Awa"), 10m, Now.AddDays(-5));

        var report = await ReportAsync(organization, role: role);
        var insights = await InsightsAsync(organization, role);

        Assert.Equal(allowed, report.IsSuccess);
        Assert.Equal(allowed, insights.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(ErrorKind.Forbidden, report.Error!.Kind);
        }
    }

    // ---- the insights

    [Fact]
    public async Task TheInsights_OnePerCustomerOverdue_WithTheFactsBehindIt()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var awa = await NewCustomerAsync(organization, "Awa Jallow");
        var notDue = await NewCustomerAsync(organization, "Not due");
        var sale = await CreditSaleAsync(organization, product, awa, 100m, Now.AddDays(-120), paid: 30m);
        await CreditSaleAsync(organization, product, notDue, 900m, Now.AddDays(10));

        var result = await InsightsAsync(organization);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var insight = Assert.Single(result.Value.Items);
        Assert.Equal(("customers.overdue_balance", "High", awa), (insight.Type, insight.Severity, insight.Subject.Id));
        Assert.Equal("collect_payment", insight.RecommendedAction.Code);
        Assert.Contains(insight.Facts, f => f.Key == "overdue_amount" && f.Value == "70");
        Assert.Contains(insight.Facts, f => f.Key == "overdue_sale_1" && f.Value.StartsWith($"{sale:D} | owes 70 |", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOrganizationsThreshold_DecidesWhichBalancesAreReported()
    {
        var organization = await NewOrganizationAsync(threshold: 100m);
        var product = await NewProductAsync(organization);
        await CreditSaleAsync(organization, product, await NewCustomerAsync(organization, "Small"), 99m, Now.AddDays(-5));
        await CreditSaleAsync(organization, product, await NewCustomerAsync(organization, "Exact"), 100m, Now.AddDays(-5));

        var insights = (await InsightsAsync(organization)).Value;
        var report = (await ReportAsync(organization)).Value;

        Assert.Equal(["Exact"], insights.Items.Select(i => i.Subject.Name).ToArray());
        Assert.Equal(100m, report.MaterialityThreshold);
        Assert.Equal(2, report.Customers.Count); // the report lists every balance
    }

    [Fact]
    public async Task WithNoThreshold_EveryOverdueBalanceIsReported()
    {
        var organization = await NewOrganizationAsync();
        await CreditSaleAsync(organization, await NewProductAsync(organization), await NewCustomerAsync(organization, "Small"), 1m, Now.AddDays(-5));

        var insights = (await InsightsAsync(organization)).Value;
        var report = (await ReportAsync(organization)).Value;

        Assert.Single(insights.Items);
        Assert.Null(report.MaterialityThreshold);
    }
}
