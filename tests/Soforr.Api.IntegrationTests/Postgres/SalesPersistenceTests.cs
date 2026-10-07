using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Sale persistence against real PostgreSQL. The aggregate is stored and read back with its lines and snapshots; the
/// constraint tests write raw SQL on purpose, to prove the database refuses invalid data even when the Domain is
/// bypassed; the concurrency test proves the sale's <c>xmin</c> token stops two writers (two people voiding at once).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SalesPersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
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

    private async Task<Guid> NewProductAsync(Guid organization, string name = "Rice 25kg")
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), name, "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), null, Now);
        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static SaleLineInput Input(Guid product, string sku, decimal quantity, decimal catalog, decimal price, decimal cost, string? reason = null) =>
        new(Guid.CreateVersion7(), product, sku, "Name of " + sku, "bag", Quantity.Create(quantity), Money.Create(catalog, Gmd), Money.Create(price, Gmd), Money.Create(cost, Gmd), reason);

    private static Sale NewSale(Guid organization, params SaleLineInput[] lines) =>
        Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Seller, Now, lines);

    private async Task SaveAsync(Guid organization, Sale sale)
    {
        await using var db = database.CreateContext(organization);
        db.Set<Sale>().Add(sale);
        await db.SaveChangesAsync();
    }

    private async Task<Sale> LoadAsync(Guid organization, Guid saleId)
    {
        await using var db = database.CreateContext(organization);
        return await db.Set<Sale>().Include(s => s.Lines).AsNoTracking().SingleAsync(s => s.Id == saleId);
    }

    // ---- the aggregate

    [Fact]
    public async Task ASaleWithItsLines_IsStoredAndReadBack_WithSnapshotsPricesAndTotals()
    {
        var organization = await NewOrganizationAsync();
        var rice = await NewProductAsync(organization);
        var oil = await NewProductAsync(organization, "Oil");
        var sale = NewSale(organization, Input(rice, "RICE-25", 10m, 12.5m, 12.5m, 7.25m), Input(oil, "OIL-5", 0.3333m, 1.5m, 1.2m, 0.8m, "Damaged label"));
        await SaveAsync(organization, sale);

        var loaded = await LoadAsync(organization, sale.Id);

        Assert.Equal((SaleStatus.Completed, Gmd, Seller, Now), (loaded.Status, loaded.Currency, loaded.CreatedByUserId, loaded.CreatedAtUtc));
        Assert.Equal(2, loaded.Lines.Count);
        var first = loaded.Lines.Single(l => l.ProductId == rice);
        Assert.Equal(("RICE-25", "Name of RICE-25", "bag"), (first.SkuSnapshot, first.NameSnapshot, first.UnitOfMeasureSnapshot));
        Assert.Equal((10m, 12.5m, 12.5m, 7.25m), (first.Quantity.Value, first.CatalogUnitPrice.Amount, first.UnitPrice.Amount, first.CostPrice.Amount));
        Assert.Equal(Gmd, first.UnitPrice.Currency);
        Assert.Null(first.PriceOverrideReason);
        var second = loaded.Lines.Single(l => l.ProductId == oil);
        Assert.Equal((1.5m, 1.2m, "Damaged label"), (second.CatalogUnitPrice.Amount, second.UnitPrice.Amount, second.PriceOverrideReason));
        Assert.Equal(sale.Total, loaded.Total);
        Assert.Equal(sale.TotalCost, loaded.TotalCost);
        Assert.Equal(sale.TotalDiscount, loaded.TotalDiscount);
        Assert.Equal(125m + 0.4m, loaded.Total.Amount); // 125.0000 + round(1.2 x 0.3333 = 0.39996) = 125.4000
        Assert.True(loaded.HasPriceOverride);
    }

    [Fact]
    public async Task ALineBuiltFromOneSharedPriceInstance_IsStoredAndReadBack()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var price = Money.Create(10m, Gmd);
        var input = new SaleLineInput(Guid.CreateVersion7(), product, "S", "N", "bag", Quantity.Create(2m), price, price, price, null);
        var sale = NewSale(organization, input);

        await SaveAsync(organization, sale);

        var line = Assert.Single((await LoadAsync(organization, sale.Id)).Lines);
        Assert.Equal((10m, 10m, 10m), (line.CatalogUnitPrice.Amount, line.UnitPrice.Amount, line.CostPrice.Amount));
    }

    [Fact]
    public async Task TheSnapshots_DoNotChange_WhenTheProductIsEditedLater()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, "Rice 25kg");
        var sale = NewSale(organization, Input(product, "RICE-25", 2m, 10m, 10m, 5m));
        await SaveAsync(organization, sale);

        await using (var db = database.CreateContext(organization))
        {
            var edited = await db.Set<Product>().SingleAsync(p => p.Id == product);
            edited.Update("NEW-SKU", "Renamed", "kg", Money.Create(99m, Gmd), Money.Create(88m, Gmd), null, Now.AddDays(1));
            await db.SaveChangesAsync();
        }

        var line = Assert.Single((await LoadAsync(organization, sale.Id)).Lines);
        Assert.Equal(("RICE-25", "Name of RICE-25", "bag"), (line.SkuSnapshot, line.NameSnapshot, line.UnitOfMeasureSnapshot));
        Assert.Equal((10m, 10m, 5m), (line.CatalogUnitPrice.Amount, line.UnitPrice.Amount, line.CostPrice.Amount));
    }

    [Fact]
    public async Task AVoid_IsPersisted_WithItsActorTimeAndReason_AndTheLinesAreUntouched()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var sale = NewSale(organization, Input(product, "RICE-25", 2m, 10m, 10m, 5m));
        await SaveAsync(organization, sale);
        var voider = Guid.CreateVersion7();

        await using (var db = database.CreateContext(organization))
        {
            var loaded = await db.Set<Sale>().Include(s => s.Lines).SingleAsync(s => s.Id == sale.Id);
            loaded.Void(voider, "Wrong customer", Now.AddHours(1));
            await db.SaveChangesAsync();
        }

        var voided = await LoadAsync(organization, sale.Id);
        Assert.Equal(SaleStatus.Voided, voided.Status);
        Assert.Equal((voider, "Wrong customer", Now.AddHours(1)), (voided.VoidedByUserId, voided.VoidReason, voided.VoidedAtUtc));
        Assert.Equal(sale.Total, voided.Total);
        Assert.Single(voided.Lines);
    }

    [Fact]
    public async Task TwoPeopleVoidingTheSameSale_CannotBothSave_TheSecondGetsAConcurrencyException()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var sale = NewSale(organization, Input(product, "RICE-25", 2m, 10m, 10m, 5m));
        await SaveAsync(organization, sale);

        await using var one = database.CreateContext(organization);
        await using var two = database.CreateContext(organization);
        var saleOne = await one.Set<Sale>().Include(s => s.Lines).SingleAsync(s => s.Id == sale.Id);
        var saleTwo = await two.Set<Sale>().Include(s => s.Lines).SingleAsync(s => s.Id == sale.Id);
        saleOne.Void(Guid.CreateVersion7(), "First", Now.AddHours(1));
        saleTwo.Void(Guid.CreateVersion7(), "Second", Now.AddHours(1));

        await one.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => two.SaveChangesAsync());
        Assert.Contains(exception.Entries, entry => entry.Entity is Sale);
        Assert.Equal("First", (await LoadAsync(organization, sale.Id)).VoidReason);
    }

    [Fact]
    public async Task ASaleAndItsLines_CannotBeDeleted()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var sale = NewSale(organization, Input(product, "RICE-25", 2m, 10m, 10m, 5m));
        await SaveAsync(organization, sale);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("DELETE FROM \"Sales\" WHERE \"Id\" = @id", ("id", sale.Id)));

        Assert.Equal("23503", exception.SqlState);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM \"Sales\" WHERE \"Id\" = '" + sale.Id + "'"));
    }

    // ---- constraints enforced by the database itself

    private async Task<(Guid Organization, Guid Product, Guid Sale)> SeedSaleAsync(string status = "Completed")
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var sale = Guid.CreateVersion7();
        await InsertSaleRawAsync(organization, sale, status, voided: status == "Voided");
        return (organization, product, sale);
    }

    [Theory]
    [InlineData("Refunded")]
    [InlineData("completed")]
    public async Task TheDatabase_RejectsAnUnknownStatus(string status)
    {
        var organization = await NewOrganizationAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, Guid.CreateVersion7(), status, voided: false));

        Assert.Equal("23514", exception.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RejectsAVoidThatContradictsTheStatus()
    {
        var organization = await NewOrganizationAsync();

        var completedWithVoid = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, Guid.CreateVersion7(), "Completed", voided: true));
        var voidedWithoutVoid = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, Guid.CreateVersion7(), "Voided", voided: false));
        var voidedWithoutReason = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, Guid.CreateVersion7(), "Voided", voided: true, reason: null));
        var blankReason = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, Guid.CreateVersion7(), "Voided", voided: true, reason: "  "));
        var voidedBeforeSale = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, Guid.CreateVersion7(), "Voided", voided: true, voidedAt: Now.AddMinutes(-1)));

        Assert.Equal("CK_Sales_Void_MatchesStatus", completedWithVoid.ConstraintName);
        Assert.Equal("CK_Sales_Void_MatchesStatus", voidedWithoutVoid.ConstraintName);
        Assert.Equal("CK_Sales_Void_MatchesStatus", voidedWithoutReason.ConstraintName);
        Assert.Equal("CK_Sales_VoidReason_NotBlank", blankReason.ConstraintName);
        Assert.Equal("CK_Sales_VoidedAt_NotBeforeSale", voidedBeforeSale.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_AcceptsEveryConsistentStatus_AndRejectsAMalformedCurrency()
    {
        const string CountSql = "SELECT count(*) FROM \"Sales\" WHERE \"CreatedByUserId\" = '00000000-0000-0000-0000-00000000cafe'";
        var before = await CountAsync(CountSql);
        await SeedSaleAsync("Completed");
        await SeedSaleAsync("Voided");
        var organization = await NewOrganizationAsync();

        var currency = await Assert.ThrowsAsync<PostgresException>(() => InsertSaleRawAsync(organization, Guid.CreateVersion7(), "Completed", voided: false, currency: "gm"));

        Assert.Equal(2, await CountAsync(CountSql) - before);
        Assert.Equal("CK_Sales_Currency_Format", currency.ConstraintName);
    }

    [Theory]
    [InlineData("quantity", "0", "CK_SaleLines_Quantity_Positive")]
    [InlineData("quantity", "-1", "CK_SaleLines_Quantity_Positive")]
    [InlineData("catalog", "-0.0001", "CK_SaleLines_CatalogUnitPrice_NotNegative")]
    [InlineData("cost", "-0.0001", "CK_SaleLines_CostPrice_NotNegative")]
    [InlineData("priceCurrency", "gmd", "CK_SaleLines_Currencies_Format")]
    [InlineData("catalogCurrency", "USD", "CK_SaleLines_Currencies_Match")]
    [InlineData("costCurrency", "USD", "CK_SaleLines_Currencies_Match")]
    [InlineData("sku", "  ", "CK_SaleLines_Snapshots_NotBlank")]
    [InlineData("name", "", "CK_SaleLines_Snapshots_NotBlank")]
    [InlineData("unit", " ", "CK_SaleLines_Snapshots_NotBlank")]
    public async Task TheDatabase_RejectsAnInvalidLine(string field, string value, string constraint)
    {
        var (organization, product, sale) = await SeedSaleAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, sale, product, (field, value)));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsANegativePrice_EvenWithAReason()
    {
        var (organization, product, sale) = await SeedSaleAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, sale, product, ("price", "-0.0001"), ("reason", "Mistake")));

        Assert.Equal("CK_SaleLines_UnitPrice_NotNegative", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsAPriceOverrideWithoutAReason_AndAReasonWithoutAnOverride()
    {
        var (organization, product, sale) = await SeedSaleAsync();
        var other = await NewProductAsync(organization);

        var priceWithoutReason = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, sale, product, ("price", "8")));
        var reasonWithoutPrice = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, sale, product, ("reason", "Because")));
        var blankReason = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, sale, product, ("price", "8"), ("reason", "  ")));
        await InsertLineRawAsync(organization, sale, product, ("price", "8"), ("reason", "Loyal customer"));
        await InsertLineRawAsync(organization, sale, other, ("price", "0"), ("reason", "Free sample"));

        Assert.Equal("CK_SaleLines_OverrideReason_MatchesPrice", priceWithoutReason.ConstraintName);
        Assert.Equal("CK_SaleLines_OverrideReason_MatchesPrice", reasonWithoutPrice.ConstraintName);
        Assert.Equal("CK_SaleLines_OverrideReason_MatchesPrice", blankReason.ConstraintName);
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM \"SaleLines\" WHERE \"SaleId\" = '" + sale + "'"));
    }

    [Fact]
    public async Task TheDatabase_RejectsTheSameProductTwiceOnOneSale_ButAllowsItOnAnotherSale()
    {
        var (organization, product, sale) = await SeedSaleAsync();
        await InsertLineRawAsync(organization, sale, product);
        var secondSale = Guid.CreateVersion7();
        await InsertSaleRawAsync(organization, secondSale, "Completed", voided: false);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, sale, product));
        await InsertLineRawAsync(organization, secondSale, product);

        Assert.Equal("23505", duplicate.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RefusesRecordsThatMixOrganizations()
    {
        var (organization, _, sale) = await SeedSaleAsync();
        var other = await NewOrganizationAsync();
        var otherProduct = await NewProductAsync(other);

        var lineWithForeignProduct = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, sale, otherProduct));
        var lineInForeignOrganization = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(other, sale, otherProduct));
        var saleForUnknownOrganization = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()));

        Assert.All(new[] { lineWithForeignProduct, lineInForeignOrganization, saleForUnknownOrganization }, exception => Assert.Equal("23503", exception.SqlState));
    }

    // ---- schema

    [Fact]
    public async Task TheTables_HaveTheExpectedColumns_AndNoPhysicalXminColumn()
    {
        var sales = await ColumnsAsync("Sales");
        var lines = await ColumnsAsync("SaleLines");

        Assert.Equal(("character varying", "NO"), sales["Status"]);
        Assert.Equal(("character varying", "NO"), sales["Currency"]);
        Assert.Equal(("timestamp with time zone", "YES"), sales["VoidedAtUtc"]);
        Assert.Equal(("uuid", "YES"), sales["VoidedByUserId"]);
        Assert.Equal(("character varying", "YES"), sales["VoidReason"]);
        Assert.Equal(9, sales.Count);
        Assert.Equal(("numeric", "NO"), lines["Quantity"]);
        Assert.Equal(("numeric", "NO"), lines["CatalogUnitPrice"]);
        Assert.Equal(("numeric", "NO"), lines["UnitPrice"]);
        Assert.Equal(("numeric", "NO"), lines["CostPrice"]);
        Assert.Equal(("character varying", "YES"), lines["PriceOverrideReason"]);
        Assert.Equal(15, lines.Count);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM pg_attribute WHERE attrelid = '\"Sales\"'::regclass AND attname = 'xmin' AND attnum > 0"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM pg_attribute WHERE attrelid = '\"Sales\"'::regclass AND attname = 'xmin' AND attnum < 0"));
    }

    [Fact]
    public async Task TheQuantityAndMoneyColumns_HaveTheExpectedPrecision()
    {
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'SaleLines' AND column_name = 'Quantity' AND numeric_precision = 18 AND numeric_scale = 4"));
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'SaleLines' AND column_name IN ('CatalogUnitPrice','UnitPrice','CostPrice') AND numeric_precision = 19 AND numeric_scale = 4"));
    }

    // ---- raw helpers

    private async Task InsertSaleRawAsync(Guid organization, Guid id, string status, bool voided, string currency = "GMD", string? reason = "Raw void", DateTimeOffset? voidedAt = null) =>
        await ExecuteAsync(
            "INSERT INTO \"Sales\" (\"Id\", \"OrganizationId\", \"Currency\", \"Status\", \"CreatedByUserId\", \"CreatedAtUtc\", \"VoidedAtUtc\", \"VoidedByUserId\", \"VoidReason\") " +
            "VALUES (@id, @org, @currency, @status, '00000000-0000-0000-0000-00000000cafe', @now, @voidedAt, @voidedBy, @reason)",
            ("id", id),
            ("org", organization),
            ("currency", currency),
            ("status", status),
            ("now", Now),
            ("voidedAt", voided ? voidedAt ?? Now.AddMinutes(1) : DBNull.Value),
            ("voidedBy", voided ? Guid.CreateVersion7() : DBNull.Value),
            ("reason", voided && reason is not null ? reason : DBNull.Value));

    private async Task InsertLineRawAsync(Guid organization, Guid sale, Guid product, params (string Field, string Value)[] overrides)
    {
        var values = new Dictionary<string, string>
        {
            ["quantity"] = "2",
            ["catalog"] = "10",
            ["price"] = "10",
            ["cost"] = "5",
            ["catalogCurrency"] = "GMD",
            ["priceCurrency"] = "GMD",
            ["costCurrency"] = "GMD",
            ["sku"] = "SKU",
            ["name"] = "Name",
            ["unit"] = "bag",
            ["reason"] = string.Empty,
        };
        foreach (var (field, value) in overrides)
        {
            values[field] = value;
        }

        static decimal Number(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

        await ExecuteAsync(
            "INSERT INTO \"SaleLines\" (\"Id\", \"OrganizationId\", \"SaleId\", \"ProductId\", \"SkuSnapshot\", \"NameSnapshot\", \"UnitOfMeasureSnapshot\", \"Quantity\", " +
            "\"CatalogUnitPrice\", \"CatalogUnitPriceCurrency\", \"UnitPrice\", \"UnitPriceCurrency\", \"CostPrice\", \"CostPriceCurrency\", \"PriceOverrideReason\") " +
            "VALUES (@id, @org, @sale, @product, @sku, @name, @unit, @quantity, @catalog, @catalogCurrency, @price, @priceCurrency, @cost, @costCurrency, @reason)",
            ("id", Guid.CreateVersion7()),
            ("org", organization),
            ("sale", sale),
            ("product", product),
            ("sku", values["sku"]),
            ("name", values["name"]),
            ("unit", values["unit"]),
            ("quantity", Number(values["quantity"])),
            ("catalog", Number(values["catalog"])),
            ("catalogCurrency", values["catalogCurrency"]),
            ("price", Number(values["price"])),
            ("priceCurrency", values["priceCurrency"]),
            ("cost", Number(values["cost"])),
            ("costCurrency", values["costCurrency"]),
            ("reason", values["reason"].Length == 0 ? DBNull.Value : values["reason"]));
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

    private async Task<int> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<Dictionary<string, (string Type, string Nullable)>> ColumnsAsync(string table)
    {
        var columns = new Dictionary<string, (string, string)>();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name = @table";
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
        }

        return columns;
    }
}
