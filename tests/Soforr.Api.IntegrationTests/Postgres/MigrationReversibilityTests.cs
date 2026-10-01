using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Kept in its own class (and therefore its own database) because it moves the schema backwards and forwards.
/// </summary>
[Trait("Category", "Integration")]
public class MigrationReversibilityTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task EveryMigration_CanBeRolledBackToAnEmptyDatabase_AndAppliedAgain()
    {
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        var known = context.Database.GetMigrations().ToArray();

        // The fixture has already applied every migration.
        Assert.True(known.Length >= 2);
        Assert.Equal(known, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await TableExistsAsync("Organizations"));

        // One step back: migrating to the oldest migration reverts every later one.
        await migrator.MigrateAsync(known[0]);
        Assert.False(await TableExistsAsync("Organizations"));
        Assert.Equal([known[0]], (await context.Database.GetAppliedMigrationsAsync()).ToArray());

        // All the way back: migration "0" reverts the oldest migration too, so no application migration remains.
        await migrator.MigrateAsync(Migration.InitialDatabase);
        Assert.False(await TableExistsAsync("Organizations"));
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());

        // And forward again, from nothing to everything.
        await context.Database.MigrateAsync();
        Assert.True(await TableExistsAsync("Organizations"));
        Assert.Equal(known, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @table";
        command.Parameters.AddWithValue("table", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}
