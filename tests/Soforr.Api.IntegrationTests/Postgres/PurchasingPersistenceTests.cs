using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;
using Soforr.Domain.Purchasing;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Supplier and purchase order persistence against real PostgreSQL. The aggregate is stored and read back with its
/// lines and snapshots; the constraint tests write raw SQL on purpose, to prove the database refuses invalid data even
/// when the Domain is bypassed; the concurrency test proves the order's <c>xmin</c> token stops two writers.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PurchasingPersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
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

    private async Task<Guid> NewProductAsync(Guid organization, string name = "Rice 25kg")
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), name, "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), null, Now);
        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<Guid> NewSupplierAsync(Guid organization, string name = "Banjul Wholesale")
    {
        var supplier = Supplier.Create(Guid.CreateVersion7(), organization, name, "+220 123 4567", Now);
        await using var db = database.CreateContext(organization);
        db.Set<Supplier>().Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static PurchaseOrder NewOrder(Guid organization, Guid supplier, params (Guid Product, string Sku, decimal Quantity, decimal Cost)[] lines)
    {
        var order = PurchaseOrder.CreateDraft(Guid.CreateVersion7(), organization, supplier, Gmd, "Quote 42", Now);
        foreach (var line in lines)
        {
            order.AddLine(Guid.CreateVersion7(), line.Product, line.Sku, "Name of " + line.Sku, "bag", Quantity.Create(line.Quantity), Money.Create(line.Cost, Gmd), Now.AddMinutes(1));
        }

        return order;
    }

    private async Task SaveAsync(Guid organization, PurchaseOrder order)
    {
        await using var db = database.CreateContext(organization);
        db.Set<PurchaseOrder>().Add(order);
        await db.SaveChangesAsync();
    }

    private async Task<PurchaseOrder> LoadAsync(Guid organization, Guid orderId)
    {
        await using var db = database.CreateContext(organization);
        return await db.Set<PurchaseOrder>().Include(o => o.Lines).AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    // ---- suppliers

    [Fact]
    public async Task ASupplier_IsStoredAndReadBack_WithEveryField()
    {
        var organization = await NewOrganizationAsync();
        var supplierId = await NewSupplierAsync(organization);

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Supplier>().SingleAsync(s => s.Id == supplierId);

        Assert.Equal("Banjul Wholesale", loaded.Name);
        Assert.Equal("+220 123 4567", loaded.ContactDetails);
        Assert.True(loaded.IsActive);
        Assert.Equal(Now, loaded.CreatedAtUtc);
    }

    [Fact]
    public async Task ASupplierUpdateAndDeactivation_ArePersisted()
    {
        var organization = await NewOrganizationAsync();
        var supplierId = await NewSupplierAsync(organization);

        await using (var db = database.CreateContext(organization))
        {
            var supplier = await db.Set<Supplier>().SingleAsync(s => s.Id == supplierId);
            supplier.Update("Renamed", null, Now.AddDays(1));
            supplier.Deactivate(Now.AddDays(2));
            await db.SaveChangesAsync();
        }

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Supplier>().SingleAsync(s => s.Id == supplierId);
        Assert.Equal("Renamed", loaded.Name);
        Assert.Null(loaded.ContactDetails);
        Assert.False(loaded.IsActive);
        Assert.Equal(Now.AddDays(2), loaded.UpdatedAtUtc);
    }

    // ---- purchase orders

    [Fact]
    public async Task AnOrderWithItsLines_IsStoredAndReadBack_WithSnapshotsAndTotals()
    {
        var organization = await NewOrganizationAsync();
        var supplier = await NewSupplierAsync(organization);
        var rice = await NewProductAsync(organization);
        var oil = await NewProductAsync(organization, "Oil");
        var order = NewOrder(organization, supplier, (rice, "RICE-25", 10m, 90.5m), (oil, "OIL-5", 0.3333m, 1.5m));
        await SaveAsync(organization, order);

        var loaded = await LoadAsync(organization, order.Id);

        Assert.Equal(supplier, loaded.SupplierId);
        Assert.Equal(Gmd, loaded.Currency);
        Assert.Equal("Quote 42", loaded.Reference);
        Assert.Equal(PurchaseOrderStatus.Draft, loaded.Status);
        Assert.Equal(Now, loaded.CreatedAtUtc);
        Assert.Equal(Now.AddMinutes(1), loaded.UpdatedAtUtc);
        Assert.Null(loaded.PlacedAtUtc);
        Assert.Equal(2, loaded.Lines.Count);

        var riceLine = loaded.Lines.Single(l => l.ProductId == rice);
        Assert.Equal("RICE-25", riceLine.SkuSnapshot);
        Assert.Equal("Name of RICE-25", riceLine.NameSnapshot);
        Assert.Equal("bag", riceLine.UnitOfMeasureSnapshot);
        Assert.Equal(10m, riceLine.OrderedQuantity.Value);
        Assert.Equal(0m, riceLine.ReceivedQuantity.Value);
        Assert.Equal(Money.Create(90.5m, Gmd), riceLine.UnitCost);
        Assert.Equal(905m, riceLine.LineTotal.Amount);
        Assert.Equal(organization, riceLine.OrganizationId);
        Assert.Equal(order.Id, riceLine.PurchaseOrderId);

        // The total is computed from the stored lines, to the same value as before the round trip.
        Assert.Equal(order.Total, loaded.Total);
        Assert.Equal(905m + 0.5m, loaded.Total.Amount);
    }

    [Fact]
    public async Task TheSnapshots_DoNotChange_WhenTheProductIsEditedLater()
    {
        var organization = await NewOrganizationAsync();
        var supplier = await NewSupplierAsync(organization);
        var rice = await NewProductAsync(organization);
        var order = NewOrder(organization, supplier, (rice, "RICE-25", 1m, 10m));
        await SaveAsync(organization, order);

        await using (var db = database.CreateContext(organization))
        {
            var product = await db.Set<Product>().SingleAsync(p => p.Id == rice);
            product.Update("RENAMED", "A completely different name", "box", Money.Create(99m, Gmd), Money.Create(77m, Gmd), null, Now.AddDays(1));
            await db.SaveChangesAsync();
        }

        var loaded = await LoadAsync(organization, order.Id);
        var line = Assert.Single(loaded.Lines);
        Assert.Equal("RICE-25", line.SkuSnapshot);
        Assert.Equal("Name of RICE-25", line.NameSnapshot);
        Assert.Equal("bag", line.UnitOfMeasureSnapshot);
        Assert.Equal(10m, line.UnitCost.Amount);
    }

    [Fact]
    public async Task ThePlacedReceivedAndCancelledStates_ArePersistedWithTheirTimestamps()
    {
        var organization = await NewOrganizationAsync();
        var supplier = await NewSupplierAsync(organization);
        var rice = await NewProductAsync(organization);
        var order = NewOrder(organization, supplier, (rice, "RICE-25", 10m, 5m));
        await SaveAsync(organization, order);

        await using (var db = database.CreateContext(organization))
        {
            var tracked = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
            tracked.Place(Now.AddHours(1));
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext(organization))
        {
            var tracked = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
            tracked.Receive([new ReceiptLine(tracked.Lines[0].Id, Quantity.Create(4))], Now.AddHours(2));
            await db.SaveChangesAsync();
        }

        var partial = await LoadAsync(organization, order.Id);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, partial.Status);
        Assert.Equal(Now.AddHours(1), partial.PlacedAtUtc);
        Assert.Equal(4m, partial.Lines[0].ReceivedQuantity.Value);
        Assert.Null(partial.ClosedAtUtc);

        await using (var db = database.CreateContext(organization))
        {
            var tracked = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
            tracked.Receive([new ReceiptLine(tracked.Lines[0].Id, Quantity.Create(6))], Now.AddHours(3));
            await db.SaveChangesAsync();
        }

        var received = await LoadAsync(organization, order.Id);
        Assert.Equal(PurchaseOrderStatus.Received, received.Status);
        Assert.Equal(Now.AddHours(3), received.ClosedAtUtc);

        var cancelled = NewOrder(organization, supplier, (rice, "RICE-25", 1m, 5m));
        await SaveAsync(organization, cancelled);
        await using (var db = database.CreateContext(organization))
        {
            var tracked = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == cancelled.Id);
            tracked.Cancel(Now.AddHours(1));
            await db.SaveChangesAsync();
        }

        var loadedCancelled = await LoadAsync(organization, cancelled.Id);
        Assert.Equal(PurchaseOrderStatus.Cancelled, loadedCancelled.Status);
        Assert.Equal(Now.AddHours(1), loadedCancelled.ClosedAtUtc);
        Assert.Null(loadedCancelled.PlacedAtUtc);
    }

    [Fact]
    public async Task ADraftsLinesCanBeAddedChangedAndRemoved_AndThePersistedOrderFollows()
    {
        var organization = await NewOrganizationAsync();
        var supplier = await NewSupplierAsync(organization);
        var rice = await NewProductAsync(organization);
        var oil = await NewProductAsync(organization, "Oil");
        var order = NewOrder(organization, supplier, (rice, "RICE-25", 1m, 5m));
        await SaveAsync(organization, order);

        await using (var db = database.CreateContext(organization))
        {
            var tracked = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
            tracked.AddLine(Guid.CreateVersion7(), oil, "OIL-5", "Oil", "can", Quantity.Create(2), Money.Create(7m, Gmd), Now.AddMinutes(5));
            tracked.UpdateLine(tracked.Lines.Single(l => l.ProductId == rice).Id, Quantity.Create(3), Money.Create(6m, Gmd), Now.AddMinutes(6));
            await db.SaveChangesAsync();
        }

        var changed = await LoadAsync(organization, order.Id);
        Assert.Equal(2, changed.Lines.Count);
        Assert.Equal(18m + 14m, changed.Total.Amount);

        await using (var db = database.CreateContext(organization))
        {
            var tracked = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
            tracked.RemoveLine(tracked.Lines.Single(l => l.ProductId == rice).Id, Now.AddMinutes(7));
            await db.SaveChangesAsync();
        }

        var removed = await LoadAsync(organization, order.Id);
        Assert.Equal([oil], removed.Lines.Select(l => l.ProductId).ToArray());
        Assert.Equal(14m, removed.Total.Amount);
    }

    // ---- optimistic concurrency

    [Fact]
    public async Task TwoPeopleReceivingTheSameOrder_CannotBothSave_TheSecondGetsAConcurrencyException()
    {
        var organization = await NewOrganizationAsync();
        var supplier = await NewSupplierAsync(organization);
        var rice = await NewProductAsync(organization);
        var order = NewOrder(organization, supplier, (rice, "RICE-25", 10m, 5m));
        order.Place(Now.AddMinutes(2));
        await SaveAsync(organization, order);

        await using var one = database.CreateContext(organization);
        await using var two = database.CreateContext(organization);
        var orderOne = await one.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
        var orderTwo = await two.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
        orderOne.Receive([new ReceiptLine(orderOne.Lines[0].Id, Quantity.Create(6))], Now.AddHours(1));
        orderTwo.Receive([new ReceiptLine(orderTwo.Lines[0].Id, Quantity.Create(6))], Now.AddHours(1));

        await one.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => two.SaveChangesAsync());
        Assert.Contains(exception.Entries, entry => entry.Entity is PurchaseOrder);
        var loaded = await LoadAsync(organization, order.Id);
        Assert.Equal(6m, loaded.Lines[0].ReceivedQuantity.Value);
    }

    // ---- constraints enforced by the database itself

    private async Task<(Guid Organization, Guid Supplier, Guid Product, Guid Order)> SeedOrderAsync(string status = "Draft")
    {
        var organization = await NewOrganizationAsync();
        var supplier = await NewSupplierAsync(organization);
        var product = await NewProductAsync(organization);
        var order = Guid.CreateVersion7();
        await InsertOrderRawAsync(organization, supplier, order, status, placed: status is "Draft" or "Cancelled" ? null : Now, closed: status is "Received" or "Cancelled" ? Now : null);
        return (organization, supplier, product, order);
    }

    [Theory]
    [InlineData("Shipped")]
    [InlineData("draft")]
    public async Task TheDatabase_RejectsAnUnknownStatus(string status)
    {
        var organization = await NewOrganizationAsync();
        var supplier = await NewSupplierAsync(organization);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderRawAsync(organization, supplier, Guid.CreateVersion7(), status, placed: null, closed: null));

        // PostgreSQL checks constraints in name order, so the placed-time check may fire before the status check; either
        // way the row is refused by a CHECK constraint.
        Assert.Equal("23514", exception.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RejectsStatusAndTimestampsThatContradictEachOther()
    {
        var organization = await NewOrganizationAsync();
        var supplier = await NewSupplierAsync(organization);

        var draftPlaced = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderRawAsync(organization, supplier, Guid.CreateVersion7(), "Draft", placed: Now, closed: null));
        var placedWithoutTime = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderRawAsync(organization, supplier, Guid.CreateVersion7(), "Placed", placed: null, closed: null));
        var receivedNotClosed = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderRawAsync(organization, supplier, Guid.CreateVersion7(), "Received", placed: Now, closed: null));
        var placedClosed = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderRawAsync(organization, supplier, Guid.CreateVersion7(), "Placed", placed: Now, closed: Now));

        Assert.Equal("CK_PurchaseOrders_PlacedAt_MatchesStatus", draftPlaced.ConstraintName);
        Assert.Equal("CK_PurchaseOrders_PlacedAt_MatchesStatus", placedWithoutTime.ConstraintName);
        Assert.Equal("CK_PurchaseOrders_ClosedAt_MatchesStatus", receivedNotClosed.ConstraintName);
        Assert.Equal("CK_PurchaseOrders_ClosedAt_MatchesStatus", placedClosed.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_AcceptsEveryConsistentStatus()
    {
        const string CountSql = "SELECT count(*) FROM \"PurchaseOrders\" WHERE \"Reference\" = 'raw'";
        var before = await CountAsync(CountSql);
        foreach (var status in new[] { "Draft", "Placed", "PartiallyReceived", "Received", "Cancelled" })
        {
            await SeedOrderAsync(status);
        }

        Assert.Equal(5, await CountAsync(CountSql) - before);
    }

    [Fact]
    public async Task TheDatabase_RejectsAMalformedCurrency_AndABlankReference()
    {
        var organization = await NewOrganizationAsync();
        var supplier = await NewSupplierAsync(organization);

        var currency = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderRawAsync(organization, supplier, Guid.CreateVersion7(), "Draft", null, null, currency: "gm"));
        var reference = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderRawAsync(organization, supplier, Guid.CreateVersion7(), "Draft", null, null, reference: "  "));

        Assert.Equal("CK_PurchaseOrders_Currency_Format", currency.ConstraintName);
        Assert.Equal("CK_PurchaseOrders_Reference_NotBlank", reference.ConstraintName);
    }

    [Theory]
    [InlineData("ordered", "0", "CK_PurchaseOrderLines_OrderedQuantity_Positive")]
    [InlineData("ordered", "-1", "CK_PurchaseOrderLines_OrderedQuantity_Positive")]
    [InlineData("received", "-0.0001", "CK_PurchaseOrderLines_ReceivedQuantity_WithinOrdered")]
    [InlineData("received", "10.0001", "CK_PurchaseOrderLines_ReceivedQuantity_WithinOrdered")]
    [InlineData("cost", "-0.0001", "CK_PurchaseOrderLines_UnitCost_NotNegative")]
    [InlineData("currency", "gmd", "CK_PurchaseOrderLines_UnitCostCurrency_Format")]
    [InlineData("sku", "  ", "CK_PurchaseOrderLines_Snapshots_NotBlank")]
    [InlineData("name", "", "CK_PurchaseOrderLines_Snapshots_NotBlank")]
    [InlineData("unit", " ", "CK_PurchaseOrderLines_Snapshots_NotBlank")]
    public async Task TheDatabase_RejectsAnInvalidLine(string field, string value, string constraint)
    {
        var (organization, _, product, order) = await SeedOrderAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, order, product, (field, value)));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_AcceptsAValidLine_AndAZeroCost_AndAFullyReceivedLine()
    {
        var (organization, _, product, order) = await SeedOrderAsync();
        var other = await NewProductAsync(organization);

        await InsertLineRawAsync(organization, order, product, ("cost", "0"), ("received", "10"));
        await InsertLineRawAsync(organization, order, other);

        Assert.Equal(2, await CountAsync("SELECT count(*) FROM \"PurchaseOrderLines\" WHERE \"PurchaseOrderId\" = '" + order + "'"));
    }

    [Fact]
    public async Task TheDatabase_RejectsTheSameProductTwiceOnOneOrder_ButAllowsItOnAnotherOrder()
    {
        var (organization, supplier, product, order) = await SeedOrderAsync();
        await InsertLineRawAsync(organization, order, product);
        var secondOrder = Guid.CreateVersion7();
        await InsertOrderRawAsync(organization, supplier, secondOrder, "Draft", null, null);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, order, product));
        await InsertLineRawAsync(organization, secondOrder, product);

        Assert.Equal("23505", duplicate.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RefusesRecordsThatMixOrganizations()
    {
        var (organization, supplier, product, order) = await SeedOrderAsync();
        var other = await NewOrganizationAsync();
        var otherSupplier = await NewSupplierAsync(other);
        var otherProduct = await NewProductAsync(other);

        var orderWithForeignSupplier = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderRawAsync(organization, otherSupplier, Guid.CreateVersion7(), "Draft", null, null));
        var lineWithForeignProduct = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(organization, order, otherProduct));
        var lineInForeignOrganization = await Assert.ThrowsAsync<PostgresException>(() => InsertLineRawAsync(other, order, otherProduct));
        var orderForUnknownSupplier = await Assert.ThrowsAsync<PostgresException>(() => InsertOrderRawAsync(organization, Guid.CreateVersion7(), Guid.CreateVersion7(), "Draft", null, null));

        Assert.All(new[] { orderWithForeignSupplier, lineWithForeignProduct, lineInForeignOrganization, orderForUnknownSupplier }, exception => Assert.Equal("23503", exception.SqlState));
        Assert.NotEqual(supplier, otherSupplier);
        Assert.NotEqual(product, otherProduct);
    }

    [Fact]
    public async Task TheDatabase_RejectsABlankSupplierName_AndABlankContact()
    {
        var organization = await NewOrganizationAsync();

        var name = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO \"Suppliers\" (\"Id\", \"OrganizationId\", \"Name\", \"IsActive\", \"CreatedAtUtc\", \"UpdatedAtUtc\") VALUES (@id, @org, '  ', true, @now, @now)",
            ("id", Guid.CreateVersion7()),
            ("org", organization),
            ("now", Now)));
        var contact = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO \"Suppliers\" (\"Id\", \"OrganizationId\", \"Name\", \"ContactDetails\", \"IsActive\", \"CreatedAtUtc\", \"UpdatedAtUtc\") VALUES (@id, @org, 'N', ' ', true, @now, @now)",
            ("id", Guid.CreateVersion7()),
            ("org", organization),
            ("now", Now)));

        Assert.Equal("CK_Suppliers_Name_NotBlank", name.ConstraintName);
        Assert.Equal("CK_Suppliers_ContactDetails_NotBlank", contact.ConstraintName);
    }

    // ---- schema

    [Fact]
    public async Task TheTables_HaveTheExpectedColumns_AndNoPhysicalXminColumn()
    {
        var orders = await ColumnsAsync("PurchaseOrders");
        var lines = await ColumnsAsync("PurchaseOrderLines");
        var suppliers = await ColumnsAsync("Suppliers");

        Assert.Equal(("character varying", "NO"), orders["Status"]);
        Assert.Equal(("character varying", "NO"), orders["Currency"]);
        Assert.Equal(("timestamp with time zone", "YES"), orders["PlacedAtUtc"]);
        Assert.Equal(("timestamp with time zone", "YES"), orders["ClosedAtUtc"]);
        Assert.Equal(10, orders.Count);
        Assert.Equal(("numeric", "NO"), lines["OrderedQuantity"]);
        Assert.Equal(("numeric", "NO"), lines["ReceivedQuantity"]);
        Assert.Equal(("numeric", "NO"), lines["UnitCost"]);
        Assert.Equal(("character varying", "NO"), lines["UnitCostCurrency"]);
        Assert.Equal(11, lines.Count);
        Assert.Equal(("character varying", "YES"), suppliers["ContactDetails"]);
        Assert.Equal(7, suppliers.Count);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM pg_attribute WHERE attrelid = '\"PurchaseOrders\"'::regclass AND attname = 'xmin' AND attnum > 0"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM pg_attribute WHERE attrelid = '\"PurchaseOrders\"'::regclass AND attname = 'xmin' AND attnum < 0"));
    }

    [Fact]
    public async Task TheQuantityAndMoneyColumns_HaveTheExpectedPrecision()
    {
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'PurchaseOrderLines' AND column_name IN ('OrderedQuantity','ReceivedQuantity') AND numeric_precision = 18 AND numeric_scale = 4"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'PurchaseOrderLines' AND column_name = 'UnitCost' AND numeric_precision = 19 AND numeric_scale = 4"));
    }

    // ---- raw helpers

    private async Task InsertOrderRawAsync(Guid organization, Guid supplier, Guid id, string status, DateTimeOffset? placed, DateTimeOffset? closed, string currency = "GMD", string reference = "raw") =>
        await ExecuteAsync(
            "INSERT INTO \"PurchaseOrders\" (\"Id\", \"OrganizationId\", \"SupplierId\", \"Currency\", \"Reference\", \"Status\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"PlacedAtUtc\", \"ClosedAtUtc\") " +
            "VALUES (@id, @org, @supplier, @currency, @reference, @status, @now, @now, @placed, @closed)",
            ("id", id),
            ("org", organization),
            ("supplier", supplier),
            ("currency", currency),
            ("reference", reference),
            ("status", status),
            ("now", Now),
            ("placed", (object?)placed ?? DBNull.Value),
            ("closed", (object?)closed ?? DBNull.Value));

    private async Task InsertLineRawAsync(Guid organization, Guid order, Guid product, params (string Field, string Value)[] overrides)
    {
        var values = new Dictionary<string, string>
        {
            ["ordered"] = "10",
            ["received"] = "0",
            ["cost"] = "5",
            ["currency"] = "GMD",
            ["sku"] = "SKU",
            ["name"] = "Name",
            ["unit"] = "bag",
        };
        foreach (var (field, value) in overrides)
        {
            values[field] = value;
        }

        await ExecuteAsync(
            "INSERT INTO \"PurchaseOrderLines\" (\"Id\", \"OrganizationId\", \"PurchaseOrderId\", \"ProductId\", \"SkuSnapshot\", \"NameSnapshot\", \"UnitOfMeasureSnapshot\", \"OrderedQuantity\", \"ReceivedQuantity\", \"UnitCost\", \"UnitCostCurrency\") " +
            "VALUES (@id, @org, @order, @product, @sku, @name, @unit, @ordered, @received, @cost, @currency)",
            ("id", Guid.CreateVersion7()),
            ("org", organization),
            ("order", order),
            ("product", product),
            ("sku", values["sku"]),
            ("name", values["name"]),
            ("unit", values["unit"]),
            ("ordered", decimal.Parse(values["ordered"], System.Globalization.CultureInfo.InvariantCulture)),
            ("received", decimal.Parse(values["received"], System.Globalization.CultureInfo.InvariantCulture)),
            ("cost", decimal.Parse(values["cost"], System.Globalization.CultureInfo.InvariantCulture)),
            ("currency", values["currency"]));
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
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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
