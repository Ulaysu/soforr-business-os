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
    public async Task TheOrganizationsMigration_CanBeRolledBack_AndAppliedAgain()
    {
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        var first = context.Database.GetMigrations().First();

        Assert.True(await TableExistsAsync("Organizations"));

        await migrator.MigrateAsync(first);
        Assert.False(await TableExistsAsync("Organizations"));
        Assert.Equal([first], (await context.Database.GetAppliedMigrationsAsync()).ToArray());

        await context.Database.MigrateAsync();
        Assert.True(await TableExistsAsync("Organizations"));
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
