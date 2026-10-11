using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Readiness means "connected and migrated". Kept in its own class (and database) because it moves the schema back and forward.
/// </summary>
[Trait("Category", "Integration")]
public class ReadinessTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private async Task<HttpStatusCode> ReadyAsync()
    {
        using var factory = new ApiFactory(database.ConnectionString).WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.DoesNotContain("igration", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal); // no detail is exposed
        return response.StatusCode;
    }

    [Fact]
    public async Task TheApplication_IsReadyOnlyWhenEveryMigrationIsApplied()
    {
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        var known = context.Database.GetMigrations().ToArray();

        Assert.Equal(HttpStatusCode.OK, await ReadyAsync());

        // One migration not applied: the database answers, but the code expects more.
        await migrator.MigrateAsync(known[^2]);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await ReadyAsync());

        // No migration applied at all (a brand new database): not ready either.
        await migrator.MigrateAsync(Migration.InitialDatabase);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await ReadyAsync());

        await context.Database.MigrateAsync();
        Assert.Equal(HttpStatusCode.OK, await ReadyAsync());
    }
}
