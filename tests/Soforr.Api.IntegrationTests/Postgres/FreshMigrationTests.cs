using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Soforr.Application.Organizations;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Applies every migration to a database created empty by this test (not one that another fixture already migrated),
/// then checks what the migrations really produced. It exists because the stock level's concurrency token is
/// PostgreSQL's own <c>xmin</c> system column, which no migration may create as a table column (a table cannot have a
/// user column of that name). Note that <c>information_schema.columns</c> never lists system columns, so the checks
/// look at <c>pg_attribute</c>, where a system column has a negative column number.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FreshMigrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 30, 15, TimeSpan.Zero);

    private sealed class FixedOrganization(Guid? id) : IOrganizationContext
    {
        public Guid? OrganizationId { get; } = id;
    }

    private sealed class CaptureCommands : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Statements.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Statements.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private string _administrationConnectionString = string.Empty;
    private string _connectionString = string.Empty;
    private readonly string _databaseName = "fresh_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        _administrationConnectionString = await SharedPostgresContainer.GetAdministrationConnectionStringAsync();
        await using var admin = new NpgsqlConnection(_administrationConnectionString);
        await admin.OpenAsync();
        await using var create = admin.CreateCommand();
#pragma warning disable CA2100 // The name is generated above from a GUID (hex digits only).
        create.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
#pragma warning restore CA2100
        await create.ExecuteNonQueryAsync();
        _connectionString = new NpgsqlConnectionStringBuilder(_administrationConnectionString) { Database = _databaseName }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_administrationConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
#pragma warning disable CA2100
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
#pragma warning restore CA2100
        await drop.ExecuteNonQueryAsync();
    }

    private AppDbContext CreateContext(Guid? organizationId = null, DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new AppDbContext(options.Options, new FixedOrganization(organizationId));
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)Convert.ChangeType(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("No value."), typeof(T), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task FromAnEmptyDatabase_EveryMigrationApplies_AndStockLevelsHasNoPhysicalXminColumn()
    {
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));

        await using (var context = CreateContext())
        {
            var known = context.Database.GetMigrations().ToArray();
            Assert.Contains(known, name => name.EndsWith("_AddInventory", StringComparison.Ordinal));

            await context.Database.MigrateAsync();

            Assert.Equal(known, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Products'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'InventoryMovements'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'StockLevels'"));

        // pg_attribute lists system columns (negative column number) as well as the table's own columns (positive).
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM pg_attribute WHERE attrelid = '\"StockLevels\"'::regclass AND attname = 'xmin' AND attnum > 0"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM pg_attribute WHERE attrelid = '\"StockLevels\"'::regclass AND attname = 'xmin' AND attnum < 0"));
        Assert.Equal(4, await ScalarAsync<int>("SELECT count(*) FROM pg_attribute WHERE attrelid = '\"StockLevels\"'::regclass AND attnum > 0 AND NOT attisdropped"));
    }

    [Fact]
    public async Task NoMigrationOperation_CreatesAnXminColumn()
    {
        await using var context = CreateContext();
        var assembly = context.GetService<IMigrationsAssembly>();

        var offenders = new List<string>();
        foreach (var (id, type) in assembly.Migrations)
        {
            var migration = (Migration)Activator.CreateInstance(type)!;
            foreach (var operation in migration.UpOperations)
            {
                switch (operation)
                {
                    case CreateTableOperation create when create.Columns.Any(IsXmin):
                        offenders.Add($"{id}: CreateTable {create.Name}");
                        break;
                    case AddColumnOperation add when IsXmin(add):
                        offenders.Add($"{id}: AddColumn {add.Table}");
                        break;
                }
            }
        }

        Assert.Empty(offenders);

        static bool IsXmin(AddColumnOperation column) => string.Equals(column.Name, "xmin", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnTheFreshDatabase_EfUsesTheSystemXminAsTheConcurrencyToken()
    {
        await using (var migrate = CreateContext())
        {
            await migrate.Database.MigrateAsync();
        }

        var organization = Guid.CreateVersion7();
        var currency = Currency.Create("GMD");
        await using (var seed = CreateContext())
        {
            seed.Set<Organization>().Add(Organization.Create(organization, "Fresh", currency, "UTC", Now));
            await seed.SaveChangesAsync();
        }

        var product = Product.Create(Guid.CreateVersion7(), organization, "FRESH-1", "Rice", "bag", Money.Create(10m, currency), Money.Create(5m, currency), null, Now);
        var opening = InventoryMovement.Record(Guid.CreateVersion7(), organization, product.Id, QuantityDelta.Create(10m), InventoryMovementReason.PurchaseReceived, InventorySourceType.PurchaseReceipt, Guid.CreateVersion7(), Guid.CreateVersion7(), null, Now);
        var level = StockLevel.Open(organization, product.Id, Now);
        level.Apply(opening);
        await using (var write = CreateContext(organization))
        {
            write.Set<Product>().Add(product);
            await write.SaveChangesAsync();
            write.Set<InventoryMovement>().Add(opening);
            write.Set<StockLevel>().Add(level);
            await write.SaveChangesAsync();
        }

        // EF reads the system column as the token: its value equals what PostgreSQL reports for the row.
        var capture = new CaptureCommands();
        await using var one = CreateContext(organization, capture);
        await using var two = CreateContext(organization);
        var levelOne = await one.Set<StockLevel>().SingleAsync();
        var levelTwo = await two.Set<StockLevel>().SingleAsync();
        var tokenBefore = one.Entry(levelOne).Property<uint>("xmin").CurrentValue;
        var storedToken = await ScalarAsync<long>("SELECT xmin::text::bigint FROM \"StockLevels\" WHERE \"ProductId\" = @id", ("id", product.Id));
        Assert.Equal(storedToken, tokenBefore);

        // Two writers of the same row: the first wins, the second is refused, and the system token changes on the update.
        var sale = (Guid source) => InventoryMovement.Record(Guid.CreateVersion7(), organization, product.Id, QuantityDelta.Create(-1m), InventoryMovementReason.SaleCompleted, InventorySourceType.Sale, source, Guid.CreateVersion7(), null, Now);
        levelOne.Apply(sale(Guid.CreateVersion7()));
        levelTwo.Apply(sale(Guid.CreateVersion7()));
        await one.SaveChangesAsync();

        // The UPDATE that EF sent filters on the system column: that is how it detects a concurrent change.
        Assert.Contains(capture.Statements, statement => statement.Contains("UPDATE \"StockLevels\"", StringComparison.Ordinal) && statement.Contains("xmin", StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => two.SaveChangesAsync());
        Assert.Contains(exception.Entries, entry => entry.Entity is StockLevel);
        Assert.NotEqual(tokenBefore, await ScalarAsync<long>("SELECT xmin::text::bigint FROM \"StockLevels\" WHERE \"ProductId\" = @id", ("id", product.Id)));
        Assert.Equal(9m, await ScalarAsync<decimal>("SELECT \"Quantity\" FROM \"StockLevels\" WHERE \"ProductId\" = @id", ("id", product.Id)));
    }
}
