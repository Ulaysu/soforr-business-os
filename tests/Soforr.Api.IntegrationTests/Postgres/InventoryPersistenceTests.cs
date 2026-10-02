using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Stock level and movement persistence against real PostgreSQL (ADR 0004). The constraint tests write raw SQL on
/// purpose: they prove the database refuses invalid data even when the Domain and the application are bypassed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InventoryPersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 30, 15, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");

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

    private static InventoryMovement Movement(Guid organization, Guid product, decimal delta, InventoryMovementReason reason = InventoryMovementReason.PurchaseReceived, InventorySourceType source = InventorySourceType.PurchaseReceipt, Guid? sourceId = null, string? note = null) =>
        InventoryMovement.Record(Guid.CreateVersion7(), organization, product, QuantityDelta.Create(delta), reason, source, sourceId ?? Guid.CreateVersion7(), Guid.CreateVersion7(), note, Now);

    // ---- round trip

    [Fact]
    public async Task AMovementAndAStockLevel_AreStoredAndReadBack_WithEveryField()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var sourceId = Guid.CreateVersion7();
        var movement = Movement(organization, product, 12.5m, InventoryMovementReason.ManualAdjustment, InventorySourceType.Adjustment, sourceId, "  Counted the shelf  ");
        var level = StockLevel.Open(organization, product, Now);
        level.Apply(movement);

        await using (var writer = database.CreateContext(organization))
        {
            writer.Set<InventoryMovement>().Add(movement);
            writer.Set<StockLevel>().Add(level);
            await writer.SaveChangesAsync();
        }

        await using var reader = database.CreateContext(organization);
        var loadedMovement = await reader.Set<InventoryMovement>().SingleAsync(m => m.Id == movement.Id);
        var loadedLevel = await reader.Set<StockLevel>().SingleAsync(l => l.ProductId == product);

        Assert.Equal(12.5m, loadedMovement.QuantityDelta.Value);
        Assert.Equal(InventoryMovementReason.ManualAdjustment, loadedMovement.Reason);
        Assert.Equal(InventorySourceType.Adjustment, loadedMovement.SourceType);
        Assert.Equal(sourceId, loadedMovement.SourceId);
        Assert.Equal(movement.ActorId, loadedMovement.ActorId);
        Assert.Equal("Counted the shelf", loadedMovement.Note);
        Assert.Equal(Now, loadedMovement.OccurredAtUtc);
        Assert.Equal(12.5m, loadedLevel.Quantity.Value);
        Assert.Equal(Now, loadedLevel.UpdatedAtUtc);
    }

    [Fact]
    public async Task TheStoredStrings_AreTheEnumNames()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var movement = Movement(organization, product, 1m);
        await using (var writer = database.CreateContext(organization))
        {
            writer.Set<InventoryMovement>().Add(movement);
            await writer.SaveChangesAsync();
        }

        Assert.Equal("PurchaseReceived", await ScalarAsync("SELECT \"Reason\" FROM \"InventoryMovements\" WHERE \"Id\" = @id", movement.Id));
        Assert.Equal("PurchaseReceipt", await ScalarAsync("SELECT \"SourceType\" FROM \"InventoryMovements\" WHERE \"Id\" = @id", movement.Id));
    }

    [Fact]
    public async Task TheLargestQuantityTheDomainAccepts_IsStoredExactly()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var movement = Movement(organization, product, Quantity.MaxValue);
        var level = StockLevel.Open(organization, product, Now);
        level.Apply(movement);
        await using (var writer = database.CreateContext(organization))
        {
            writer.Set<InventoryMovement>().Add(movement);
            writer.Set<StockLevel>().Add(level);
            await writer.SaveChangesAsync();
        }

        await using var reader = database.CreateContext(organization);
        Assert.Equal(Quantity.MaxValue, (await reader.Set<StockLevel>().SingleAsync(l => l.ProductId == product)).Quantity.Value);
        Assert.Equal(-Quantity.MaxValue, (await ApplyAndReadDeltaAsync(organization, product, -Quantity.MaxValue)));
    }

    private async Task<decimal> ApplyAndReadDeltaAsync(Guid organization, Guid product, decimal delta)
    {
        var movement = Movement(organization, product, delta, InventoryMovementReason.SaleCompleted, InventorySourceType.Sale);
        await using var writer = database.CreateContext(organization);
        writer.Set<InventoryMovement>().Add(movement);
        await writer.SaveChangesAsync();
        await using var reader = database.CreateContext(organization);
        return (await reader.Set<InventoryMovement>().SingleAsync(m => m.Id == movement.Id)).QuantityDelta.Value;
    }

    // ---- append-only

    [Fact]
    public async Task ARecordedMovement_CannotBeChanged_OrDeleted_ThroughTheApplication()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var movement = Movement(organization, product, 5m);
        await using (var writer = database.CreateContext(organization))
        {
            writer.Set<InventoryMovement>().Add(movement);
            await writer.SaveChangesAsync();
        }

        await using (var changer = database.CreateContext(organization))
        {
            var loaded = await changer.Set<InventoryMovement>().SingleAsync(m => m.Id == movement.Id);
            changer.Entry(loaded).Property(m => m.Note).CurrentValue = "edited";
            await Assert.ThrowsAsync<AppendOnlyViolationException>(() => changer.SaveChangesAsync());
        }

        await using (var deleter = database.CreateContext(organization))
        {
            deleter.Set<InventoryMovement>().Remove(await deleter.Set<InventoryMovement>().SingleAsync(m => m.Id == movement.Id));
            await Assert.ThrowsAsync<AppendOnlyViolationException>(() => deleter.SaveChangesAsync());
        }

        await using var verify = database.CreateContext(organization);
        var stored = await verify.Set<InventoryMovement>().SingleAsync(m => m.Id == movement.Id);
        Assert.Equal(5m, stored.QuantityDelta.Value);
        Assert.Null(stored.Note);
    }

    [Fact]
    public async Task TheAppendOnlyRule_AppliesEvenWithoutAnOrganization()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var movement = Movement(organization, product, 5m);
        await using (var writer = database.CreateContext(organization))
        {
            writer.Set<InventoryMovement>().Add(movement);
            await writer.SaveChangesAsync();
        }

        await using var nobody = database.CreateContext();
        nobody.Set<InventoryMovement>().Attach(movement).State = EntityState.Deleted;

        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => nobody.SaveChangesAsync());
    }

    // ---- concurrency token

    [Fact]
    public async Task TwoWritersOfTheSameStockLevel_CannotBothSave_TheSecondGetsAConcurrencyException()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await using (var seed = database.CreateContext(organization))
        {
            var first = Movement(organization, product, 10m);
            var level = StockLevel.Open(organization, product, Now);
            level.Apply(first);
            seed.Set<InventoryMovement>().Add(first);
            seed.Set<StockLevel>().Add(level);
            await seed.SaveChangesAsync();
        }

        await using var one = database.CreateContext(organization);
        await using var two = database.CreateContext(organization);
        var levelOne = await one.Set<StockLevel>().SingleAsync(l => l.ProductId == product);
        var levelTwo = await two.Set<StockLevel>().SingleAsync(l => l.ProductId == product);
        levelOne.Apply(Movement(organization, product, -3m, InventoryMovementReason.SaleCompleted, InventorySourceType.Sale));
        levelTwo.Apply(Movement(organization, product, -4m, InventoryMovementReason.SaleCompleted, InventorySourceType.Sale));

        await one.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => two.SaveChangesAsync());
        await using var verify = database.CreateContext(organization);
        Assert.Equal(7m, (await verify.Set<StockLevel>().SingleAsync(l => l.ProductId == product)).Quantity.Value);
    }

    // ---- constraints enforced by the database itself

    [Fact]
    public async Task TheDatabase_RejectsNegativeStock()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO \"StockLevels\" (\"OrganizationId\", \"ProductId\", \"Quantity\", \"UpdatedAtUtc\") VALUES (@organization, @product, -0.0001, @now)",
            ("organization", organization),
            ("product", product),
            ("now", Now)));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("CK_StockLevels_Quantity_NotNegative", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsASecondStockLevelForTheSameProduct()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await InsertLevelRawAsync(organization, product);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertLevelRawAsync(organization, product));

        Assert.Equal("23505", exception.SqlState);
    }

    [Theory]
    [InlineData("PurchaseReceived", "PurchaseReceipt", "-1", "CK_InventoryMovements_Reason_MatchesSign")]
    [InlineData("Return", "Sale", "-1", "CK_InventoryMovements_Reason_MatchesSign")]
    [InlineData("SaleCompleted", "Sale", "1", "CK_InventoryMovements_Reason_MatchesSign")]
    [InlineData("Damage", "Adjustment", "1", "CK_InventoryMovements_Reason_MatchesSign")]
    [InlineData("PurchaseReceived", "PurchaseReceipt", "0", "CK_InventoryMovements_QuantityDelta_NotZero")]
    [InlineData("ManualAdjustment", "Adjustment", "0", "CK_InventoryMovements_QuantityDelta_NotZero")]
    [InlineData("Theft", "Adjustment", "-1", "CK_InventoryMovements_Reason_Known")]
    [InlineData("PurchaseReceived", "Mystery", "1", "CK_InventoryMovements_SourceType_Known")]
    public async Task TheDatabase_RejectsAMovementThatBreaksTheReasonSignOrKnownValueRules(string reason, string sourceType, string delta, string constraint)
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertMovementRawAsync(organization, product, delta, reason, sourceType, note: "n"));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Theory]
    [InlineData("1", "PurchaseReceived", "PurchaseReceipt")]
    [InlineData("1", "Return", "Sale")]
    [InlineData("-1", "SaleCompleted", "Sale")]
    [InlineData("-1", "Damage", "Adjustment")]
    [InlineData("5", "ManualAdjustment", "Adjustment")]
    [InlineData("-5", "ManualAdjustment", "Adjustment")]
    public async Task TheDatabase_AcceptsEveryAllowedCombinationOfReasonAndSign(string delta, string reason, string sourceType)
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        await InsertMovementRawAsync(organization, product, delta, reason, sourceType, note: "counted");

        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM \"InventoryMovements\" WHERE \"ProductId\" = @id", product));
    }

    [Theory]
    [InlineData(null, "CK_InventoryMovements_Note_RequiredForAdjustment")]
    [InlineData("   ", "CK_InventoryMovements_Note_NotBlank")]
    public async Task TheDatabase_RequiresANoteForAManualAdjustment(string? note, string constraint)
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertMovementRawAsync(organization, product, "3", "ManualAdjustment", "Adjustment", note));

        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsABlankNote_AndANoteThatIsTooLong()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var blank = await Assert.ThrowsAsync<PostgresException>(() => InsertMovementRawAsync(organization, product, "1", "PurchaseReceived", "PurchaseReceipt", "  "));
        var tooLong = await Assert.ThrowsAsync<PostgresException>(() => InsertMovementRawAsync(organization, product, "1", "PurchaseReceived", "PurchaseReceipt", new string('n', InventoryMovement.MaxNoteLength + 1)));

        Assert.Equal("CK_InventoryMovements_Note_NotBlank", blank.ConstraintName);
        Assert.Equal("22001", tooLong.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RejectsTheSameMovementFromTheSameSourceTwice_ButAllowsADifferentReasonOrProduct()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var otherProduct = await NewProductAsync(organization);
        var source = Guid.CreateVersion7();
        await InsertMovementRawAsync(organization, product, "5", "PurchaseReceived", "PurchaseReceipt", null, source);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertMovementRawAsync(organization, product, "5", "PurchaseReceived", "PurchaseReceipt", null, source));
        await InsertMovementRawAsync(organization, product, "-1", "Damage", "PurchaseReceipt", null, source);
        await InsertMovementRawAsync(organization, otherProduct, "5", "PurchaseReceived", "PurchaseReceipt", null, source);

        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("IX_InventoryMovements_Idempotency", duplicate.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RefusesAMovementOrLevelWhoseOrganizationIsNotTheProductsOrganization()
    {
        var organization = await NewOrganizationAsync();
        var other = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var movement = await Assert.ThrowsAsync<PostgresException>(() => InsertMovementRawAsync(other, product, "1", "PurchaseReceived", "PurchaseReceipt", null));
        var level = await Assert.ThrowsAsync<PostgresException>(() => InsertLevelRawAsync(other, product));
        var unknown = await Assert.ThrowsAsync<PostgresException>(() => InsertMovementRawAsync(organization, Guid.CreateVersion7(), "1", "PurchaseReceived", "PurchaseReceipt", null));

        Assert.Equal("23503", movement.SqlState);
        Assert.Equal("23503", level.SqlState);
        Assert.Equal("23503", unknown.SqlState);
    }

    // ---- schema

    [Fact]
    public async Task TheTables_HaveTheExpectedColumns()
    {
        var movements = await ColumnsAsync("InventoryMovements");
        var levels = await ColumnsAsync("StockLevels");

        Assert.Equal(("numeric", "NO"), movements["QuantityDelta"]);
        Assert.Equal(("character varying", "NO"), movements["Reason"]);
        Assert.Equal(("character varying", "NO"), movements["SourceType"]);
        Assert.Equal(("uuid", "NO"), movements["SourceId"]);
        Assert.Equal(("uuid", "NO"), movements["ActorId"]);
        Assert.Equal(("character varying", "YES"), movements["Note"]);
        Assert.Equal(10, movements.Count);
        Assert.Equal(("numeric", "NO"), levels["Quantity"]);
        Assert.Equal(4, levels.Count);
        Assert.DoesNotContain("xmin", levels.Keys);
    }

    [Fact]
    public async Task TheQuantityColumns_AreNumeric18x4()
    {
        Assert.Equal("18,4", await ScalarAsync("SELECT numeric_precision || ',' || numeric_scale FROM information_schema.columns WHERE table_name = 'InventoryMovements' AND column_name = 'QuantityDelta'"));
        Assert.Equal("18,4", await ScalarAsync("SELECT numeric_precision || ',' || numeric_scale FROM information_schema.columns WHERE table_name = 'StockLevels' AND column_name = 'Quantity'"));
    }

    // ---- reconciliation

    /// <summary>The operational query of ADR 0004: every stock level whose quantity differs from the sum of its movements. It must return nothing.</summary>
    public const string ReconciliationSql =
        "SELECT l.\"OrganizationId\", l.\"ProductId\", l.\"Quantity\", COALESCE(SUM(m.\"QuantityDelta\"), 0) AS expected " +
        "FROM \"StockLevels\" l LEFT JOIN \"InventoryMovements\" m ON m.\"OrganizationId\" = l.\"OrganizationId\" AND m.\"ProductId\" = l.\"ProductId\" " +
        "GROUP BY l.\"OrganizationId\", l.\"ProductId\", l.\"Quantity\" HAVING l.\"Quantity\" <> COALESCE(SUM(m.\"QuantityDelta\"), 0)";

    [Fact]
    public async Task TheReconciliationQuery_ReportsAStockLevelThatDoesNotMatchItsMovements()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await ExecuteAsync(
            "INSERT INTO \"StockLevels\" (\"OrganizationId\", \"ProductId\", \"Quantity\", \"UpdatedAtUtc\") VALUES (@organization, @product, 4, @now)",
            ("organization", organization),
            ("product", product),
            ("now", Now));

        var mismatches = await ReconciliationMismatchesAsync(database.ConnectionString);

        Assert.Contains(product, mismatches);
    }

    public static async Task<List<Guid>> ReconciliationMismatchesAsync(string connectionString)
    {
        var products = new List<Guid>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = ReconciliationSql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            products.Add(reader.GetGuid(1));
        }

        return products;
    }

    // ---- raw helpers

    private async Task InsertLevelRawAsync(Guid organization, Guid product) =>
        await ExecuteAsync(
            "INSERT INTO \"StockLevels\" (\"OrganizationId\", \"ProductId\", \"Quantity\", \"UpdatedAtUtc\") VALUES (@organization, @product, 0, @now)",
            ("organization", organization),
            ("product", product),
            ("now", Now));

    private async Task InsertMovementRawAsync(Guid organization, Guid product, string delta, string reason, string sourceType, string? note, Guid? sourceId = null) =>
        await ExecuteAsync(
            "INSERT INTO \"InventoryMovements\" (\"Id\", \"OrganizationId\", \"ProductId\", \"QuantityDelta\", \"Reason\", \"SourceType\", \"SourceId\", \"ActorId\", \"Note\", \"OccurredAtUtc\") " +
            "VALUES (@id, @organization, @product, @delta, @reason, @sourceType, @sourceId, @actor, @note, @now)",
            ("id", Guid.CreateVersion7()),
            ("organization", organization),
            ("product", product),
            ("delta", decimal.Parse(delta, System.Globalization.CultureInfo.InvariantCulture)),
            ("reason", reason),
            ("sourceType", sourceType),
            ("sourceId", sourceId ?? Guid.CreateVersion7()),
            ("actor", Guid.CreateVersion7()),
            ("note", (object?)note ?? DBNull.Value),
            ("now", Now));

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

    private async Task<string?> ScalarAsync(string sql, Guid? id = null)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is { } value)
        {
            command.Parameters.AddWithValue("id", value);
        }

        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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
