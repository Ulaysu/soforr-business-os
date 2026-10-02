using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Product persistence against real PostgreSQL. The constraint tests write raw SQL on purpose: they prove the
/// database rejects invalid data even when the Domain is bypassed. The SKU tests prove ADR 0006: unique per
/// organization, compared case-insensitively, entered casing preserved, enforced by the database itself.
/// </summary>
[Trait("Category", "Integration")]
public class ProductPersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Created = new(2026, 5, 4, 9, 30, 15, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");

    private async Task<Guid> NewOrganizationAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Gmd, "Europe/London", Created));
        await db.SaveChangesAsync();
        return id;
    }

    private static Product NewProduct(Guid organizationId, string sku = "ABC-123", Guid? id = null, ReorderSettings? reorder = null) =>
        Product.Create(
            id ?? Guid.CreateVersion7(),
            organizationId,
            sku,
            "Rice 25kg",
            "bag",
            Money.Create(125.5m, Gmd),
            Money.Create(90.25m, Gmd),
            reorder,
            Created);

    private async Task SaveAsync(Guid organizationId, Product product)
    {
        await using var db = database.CreateContext(organizationId);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
    }

    // ---- round trip

    [Fact]
    public async Task AProduct_IsStoredAndReadBack_WithEveryField()
    {
        var organization = await NewOrganizationAsync();
        var original = NewProduct(organization, reorder: ReorderSettings.Create(Quantity.Create(12.5m), 7));
        await SaveAsync(organization, original);

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Product>().SingleAsync(product => product.Id == original.Id);

        Assert.Equal(organization, loaded.OrganizationId);
        Assert.Equal("ABC-123", loaded.Sku);
        Assert.Equal("Rice 25kg", loaded.Name);
        Assert.Equal("bag", loaded.UnitOfMeasure);
        Assert.Equal(Money.Create(125.5m, Gmd), loaded.SellingPrice);
        Assert.Equal(Money.Create(90.25m, Gmd), loaded.CostPrice);
        Assert.Equal(ReorderSettings.Create(Quantity.Create(12.5m), 7), loaded.ReorderSettings);
        Assert.True(loaded.IsActive);
        Assert.Equal(Created, loaded.CreatedAtUtc);
        Assert.Equal(Created, loaded.UpdatedAtUtc);
    }

    [Fact]
    public async Task AProductWithoutReorderSettings_ReadsBackWithoutThem()
    {
        var organization = await NewOrganizationAsync();
        var original = NewProduct(organization);
        await SaveAsync(organization, original);

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Product>().SingleAsync(product => product.Id == original.Id);

        Assert.Null(loaded.ReorderSettings);
    }

    [Fact]
    public async Task MoneyAndQuantities_KeepTheirFullFourDecimalPlaces_AndAZeroCostIsStored()
    {
        var organization = await NewOrganizationAsync();
        var original = Product.Create(
            Guid.CreateVersion7(),
            organization,
            "PRECISE",
            "Precise",
            "kg",
            Money.Create(0.0001m, Gmd),
            Money.Zero(Gmd),
            ReorderSettings.Create(Quantity.Create(Quantity.MaxValue), 0),
            Created);
        await SaveAsync(organization, original);

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Product>().SingleAsync(product => product.Id == original.Id);

        Assert.Equal(0.0001m, loaded.SellingPrice.Amount);
        Assert.Equal(0m, loaded.CostPrice.Amount);
        Assert.Equal(Quantity.MaxValue, loaded.ReorderSettings!.ReorderPoint.Value);
        Assert.Equal(0, loaded.ReorderSettings.LeadTimeDays);
    }

    [Fact]
    public async Task TheLargestMoneyTheDomainAccepts_IsStoredAndReadBackExactly()
    {
        var organization = await NewOrganizationAsync();
        var largest = Product.Create(Guid.CreateVersion7(), organization, "MAX-MONEY", "Max", "each", Money.Create(Money.MaxAmount, Gmd), Money.Create(Money.MaxAmount, Gmd), null, Created);

        await SaveAsync(organization, largest);

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Product>().SingleAsync(product => product.Id == largest.Id);
        Assert.Equal(Money.MaxAmount, loaded.SellingPrice.Amount);
        Assert.Equal(Money.MaxAmount, loaded.CostPrice.Amount);
        Assert.Equal(999_999_999_999_999.9999m, loaded.SellingPrice.Amount);
    }

    [Fact]
    public async Task TheDatabaseColumn_RejectsTheFirstAmountTheDomainRejects_SoTheTwoLimitsAreOneContract()
    {
        var organization = await NewOrganizationAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, Guid.CreateVersion7(), ("selling", "1000000000000000")));

        Assert.Equal("22003", exception.SqlState);
    }

    [Fact]
    public async Task AnUpdateAndADeactivation_ArePersisted()
    {
        var organization = await NewOrganizationAsync();
        var original = NewProduct(organization);
        await SaveAsync(organization, original);
        var later = Created.AddDays(2);

        await using (var updater = database.CreateContext(organization))
        {
            var product = await updater.Set<Product>().SingleAsync(p => p.Id == original.Id);
            product.Update("Abc-123", "Rice 50kg", "bag", Money.Create(200m, Gmd), Money.Create(150m, Gmd), ReorderSettings.Create(Quantity.Create(5m), 3), later);
            product.Deactivate(later.AddHours(1));
            await updater.SaveChangesAsync();
        }

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Product>().SingleAsync(p => p.Id == original.Id);
        Assert.Equal("Abc-123", loaded.Sku);
        Assert.Equal("Rice 50kg", loaded.Name);
        Assert.Equal(200m, loaded.SellingPrice.Amount);
        Assert.Equal(5m, loaded.ReorderSettings!.ReorderPoint.Value);
        Assert.False(loaded.IsActive);
        Assert.Equal(later.AddHours(1), loaded.UpdatedAtUtc);
        Assert.Equal(Created, loaded.CreatedAtUtc);
    }

    [Fact]
    public async Task ClearingTheReorderSettings_IsPersisted()
    {
        var organization = await NewOrganizationAsync();
        var original = NewProduct(organization, reorder: ReorderSettings.Create(Quantity.Create(5m), 3));
        await SaveAsync(organization, original);

        await using (var updater = database.CreateContext(organization))
        {
            var product = await updater.Set<Product>().SingleAsync(p => p.Id == original.Id);
            product.Update("ABC-123", "Rice 25kg", "bag", Money.Create(125.5m, Gmd), Money.Create(90.25m, Gmd), null, Created.AddDays(1));
            await updater.SaveChangesAsync();
        }

        await using var reader = database.CreateContext(organization);
        Assert.Null((await reader.Set<Product>().SingleAsync(p => p.Id == original.Id)).ReorderSettings);
    }

    // ---- SKU uniqueness (ADR 0006)

    [Fact]
    public async Task TheEnteredSkuCasing_IsPreserved_AndTheSkuIsStoredTrimmed()
    {
        var organization = await NewOrganizationAsync();
        var original = NewProduct(organization, sku: "  AbC-123  ");
        await SaveAsync(organization, original);

        var stored = await ScalarAsync("SELECT \"Sku\" FROM \"Products\" WHERE \"Id\" = @id", ("id", original.Id));

        Assert.Equal("AbC-123", stored);
    }

    [Theory]
    [InlineData("ABC-123", "abc-123")]
    [InlineData("abc-123", "ABC-123")]
    [InlineData("AbC-123", "aBc-123")]
    [InlineData("ABC-123", "ABC-123")]
    public async Task TheDatabase_RejectsASkuThatDiffersOnlyByCase_InTheSameOrganization(string first, string second)
    {
        var organization = await NewOrganizationAsync();
        await SaveAsync(organization, NewProduct(organization, first));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(organization, NewProduct(organization, second)));

        var inner = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("23505", inner.SqlState);
        Assert.Equal("IX_Products_OrganizationId_SkuKey", inner.ConstraintName);
    }

    [Fact]
    public async Task TheSameSku_IsAllowed_InDifferentOrganizations()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        await SaveAsync(first, NewProduct(first, "SHARED-1"));

        await SaveAsync(second, NewProduct(second, "shared-1"));

        await using var asSecond = database.CreateContext(second);
        Assert.Equal("shared-1", (await asSecond.Set<Product>().SingleAsync()).Sku);
    }

    [Fact]
    public async Task ADeactivatedProduct_StillReservesItsSku_BecauseNoExceptionHasBeenDecided()
    {
        var organization = await NewOrganizationAsync();
        var original = NewProduct(organization, "OLD-1");
        await SaveAsync(organization, original);
        await using (var db = database.CreateContext(organization))
        {
            var product = await db.Set<Product>().SingleAsync(p => p.Id == original.Id);
            product.Deactivate(Created.AddDays(1));
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(organization, NewProduct(organization, "old-1")));
    }

    [Fact]
    public async Task ChangingAProductsSkuToOneThatExistsInAnotherCase_IsRejected()
    {
        var organization = await NewOrganizationAsync();
        await SaveAsync(organization, NewProduct(organization, "ONE"));
        var other = NewProduct(organization, "TWO");
        await SaveAsync(organization, other);

        await using var db = database.CreateContext(organization);
        var product = await db.Set<Product>().SingleAsync(p => p.Id == other.Id);
        product.Update("one", product.Name, product.UnitOfMeasure, product.SellingPrice, product.CostPrice, null, Created.AddDays(1));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TwoConcurrentInserts_OfTheSameSkuInDifferentCases_LetExactlyOneWin()
    {
        var organization = await NewOrganizationAsync();

        var results = await Task.WhenAll(
            TrySaveAsync(organization, NewProduct(organization, "RACE-1")),
            TrySaveAsync(organization, NewProduct(organization, "race-1")),
            TrySaveAsync(organization, NewProduct(organization, "Race-1")),
            TrySaveAsync(organization, NewProduct(organization, "rACE-1")));

        Assert.Equal(1, results.Count(saved => saved));
        await using var reader = database.CreateContext(organization);
        Assert.Equal(1, await reader.Set<Product>().CountAsync());
    }

    [Fact]
    public async Task ALookupBySku_IsCaseInsensitive_UsingTheSameComparisonAsTheDatabase()
    {
        var organization = await NewOrganizationAsync();
        var original = NewProduct(organization, "Lookup-Me");
        await SaveAsync(organization, original);

        await using var reader = database.CreateContext(organization);
        var wanted = "LOOKUP-ME";
        // The comparison key is the database's own lower("Sku") (the column behind the unique index), compared with
        // the lowercased input.
        var key = wanted.ToLowerInvariant();
        var found = await reader.Set<Product>().SingleOrDefaultAsync(p => EF.Property<string>(p, "SkuKey") == key);

        Assert.Equal(original.Id, found?.Id);
    }

    // ---- schema

    [Fact]
    public async Task TheTable_HasTheExpectedColumnTypesAndNullability()
    {
        var columns = new Dictionary<string, (string Type, int? Length, string Nullable)>();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT column_name, data_type, character_maximum_length, is_nullable FROM information_schema.columns WHERE table_name = 'Products'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetString(3));
            }
        }

        Assert.Equal(("uuid", null, "NO"), columns["Id"]);
        Assert.Equal(("uuid", null, "NO"), columns["OrganizationId"]);
        Assert.Equal(("character varying", 64, "NO"), columns["Sku"]);
        Assert.Equal(("character varying", 200, "NO"), columns["Name"]);
        Assert.Equal(("character varying", 32, "NO"), columns["UnitOfMeasure"]);
        Assert.Equal(("numeric", null, "NO"), columns["SellingPrice"]);
        Assert.Equal(("character varying", 3, "NO"), columns["SellingPriceCurrency"]);
        Assert.Equal(("numeric", null, "NO"), columns["CostPrice"]);
        Assert.Equal(("character varying", 3, "NO"), columns["CostPriceCurrency"]);
        Assert.Equal(("numeric", null, "YES"), columns["ReorderPoint"]);
        Assert.Equal(("integer", null, "YES"), columns["ReorderLeadTimeDays"]);
        Assert.Equal(("boolean", null, "NO"), columns["IsActive"]);
        Assert.Equal(("timestamp with time zone", null, "NO"), columns["CreatedAtUtc"]);
        Assert.Equal(("timestamp with time zone", null, "NO"), columns["UpdatedAtUtc"]);
        Assert.Equal(("character varying", 64, "YES"), columns["SkuKey"]);
        Assert.Equal(15, columns.Count);
    }

    [Fact]
    public async Task TheMoneyColumns_AreNumeric19x4_AndTheQuantityColumnIsNumeric18x4()
    {
        var precisions = new Dictionary<string, (int Precision, int Scale)>();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT column_name, numeric_precision, numeric_scale FROM information_schema.columns WHERE table_name = 'Products' AND data_type = 'numeric'";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            precisions[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        }

        Assert.Equal((19, 4), precisions["SellingPrice"]);
        Assert.Equal((19, 4), precisions["CostPrice"]);
        Assert.Equal((18, 4), precisions["ReorderPoint"]);
    }

    // ---- constraints enforced by the database itself

    [Fact]
    public async Task TheDatabase_RejectsAProductForAnOrganizationThatDoesNotExist()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), Guid.CreateVersion7()));

        Assert.Equal("23503", exception.SqlState);
    }

    [Theory]
    [InlineData("sku", "", "CK_Products_Sku_NotBlank")]
    [InlineData("sku", "   ", "CK_Products_Sku_NotBlank")]
    [InlineData("name", "", "CK_Products_Name_NotBlank")]
    [InlineData("name", "  ", "CK_Products_Name_NotBlank")]
    [InlineData("unit", "", "CK_Products_UnitOfMeasure_NotBlank")]
    [InlineData("unit", " ", "CK_Products_UnitOfMeasure_NotBlank")]
    [InlineData("selling", "-0.0001", "CK_Products_SellingPrice_NotNegative")]
    [InlineData("cost", "-1", "CK_Products_CostPrice_NotNegative")]
    [InlineData("sellingCurrency", "gm", "CK_Products_PriceCurrency_Format")]
    [InlineData("costCurrency", "gmd", "CK_Products_PriceCurrency_Format")]
    [InlineData("costCurrency", "EUR", "CK_Products_PriceCurrency_Same")]
    [InlineData("reorderPoint", "-1", "CK_Products_ReorderPoint_NotNegative")]
    [InlineData("leadTime", "-1", "CK_Products_ReorderLeadTimeDays_NotNegative")]
    public async Task TheDatabase_RejectsInvalidValues(string field, string value, string constraint)
    {
        var organization = await NewOrganizationAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, Guid.CreateVersion7(), (field, value)));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsValuesThatAreTooLong()
    {
        var organization = await NewOrganizationAsync();

        var sku = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, Guid.CreateVersion7(), ("sku", new string('s', Product.MaxSkuLength + 1))));
        var name = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, Guid.CreateVersion7(), ("name", new string('n', Product.MaxNameLength + 1))));
        var unit = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, Guid.CreateVersion7(), ("unit", new string('u', Product.MaxUnitOfMeasureLength + 1))));

        Assert.All([sku, name, unit], exception => Assert.Equal("22001", exception.SqlState));
    }

    [Fact]
    public async Task AValidRowWrittenWithRawSql_IsAccepted_AndReadableThroughTheDomainModel()
    {
        var organization = await NewOrganizationAsync();
        var id = Guid.CreateVersion7();
        await InsertRawAsync(organization, id, ("sku", "Raw-1"), ("reorderPoint", "3"), ("leadTime", "2"));

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Product>().SingleAsync(p => p.Id == id);

        Assert.Equal("Raw-1", loaded.Sku);
        Assert.Equal(3m, loaded.ReorderSettings!.ReorderPoint.Value);
    }

    private async Task<bool> TrySaveAsync(Guid organizationId, Product product)
    {
        try
        {
            await SaveAsync(organizationId, product);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private async Task InsertRawAsync(Guid organizationId, Guid id, params (string Field, string Value)[] overrides)
    {
        var values = new Dictionary<string, string>
        {
            ["sku"] = "RAW-" + id.ToString("N"),
            ["name"] = "Raw product",
            ["unit"] = "each",
            ["selling"] = "10",
            ["cost"] = "5",
            ["sellingCurrency"] = "GMD",
            ["costCurrency"] = "GMD",
        };
        foreach (var (field, value) in overrides)
        {
            values[field] = value;
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO \"Products\" (\"Id\", \"OrganizationId\", \"Sku\", \"Name\", \"UnitOfMeasure\", \"SellingPrice\", \"SellingPriceCurrency\", " +
            "\"CostPrice\", \"CostPriceCurrency\", \"ReorderPoint\", \"ReorderLeadTimeDays\", \"IsActive\", \"CreatedAtUtc\", \"UpdatedAtUtc\") " +
            "VALUES (@id, @organization, @sku, @name, @unit, @selling, @sellingCurrency, @cost, @costCurrency, @reorderPoint, @leadTime, true, @created, @created)";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("sku", values["sku"]);
        command.Parameters.AddWithValue("name", values["name"]);
        command.Parameters.AddWithValue("unit", values["unit"]);
        command.Parameters.AddWithValue("selling", decimal.Parse(values["selling"], System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("sellingCurrency", values["sellingCurrency"]);
        command.Parameters.AddWithValue("cost", decimal.Parse(values["cost"], System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("costCurrency", values["costCurrency"]);
        command.Parameters.AddWithValue("reorderPoint", values.TryGetValue("reorderPoint", out var point) ? decimal.Parse(point, System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("leadTime", values.TryGetValue("leadTime", out var days) ? int.Parse(days, System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("created", Created);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (string?)await command.ExecuteScalarAsync();
    }
}
