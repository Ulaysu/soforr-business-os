using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>Tests of the PostgreSQL harness itself; they also prove the application's migrations and wiring on a real database.</summary>
[Trait("Category", "Integration")]
public class DatabaseHarnessTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task Migrations_ApplyToAnEmptyDatabase_AndNothingIsPending()
    {
        await using var context = database.CreateContext();

        var known = context.Database.GetMigrations().ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var pending = (await context.Database.GetPendingMigrationsAsync()).ToArray();

        Assert.NotEmpty(known);
        Assert.Equal(known, applied);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task EachClassHasItsOwnGeneratedDatabase()
    {
        await using var context = database.CreateContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database()";

        var current = (string?)await command.ExecuteScalarAsync();

        Assert.Equal(database.DatabaseName, current);
        Assert.StartsWith("test_", current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheApplication_ReportsItselfReady_AgainstARealDatabase()
    {
        using var factory = new ApiFactory(database.ConnectionString).WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void TheTwoTenantStub_ProvidesTwoDistinctOrganizations()
    {
        Assert.NotEqual(Guid.Empty, TestTenants.OrganizationA);
        Assert.NotEqual(Guid.Empty, TestTenants.OrganizationB);
        Assert.NotEqual(TestTenants.OrganizationA, TestTenants.OrganizationB);
    }
}
