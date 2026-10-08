using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Soforr.Domain.Common;
using Soforr.Domain.Offline;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>The offline tables on real PostgreSQL (ADR 0008): the constraints the Domain also enforces, tenant scoping, append-only contacts, and the migration (including the backfill of existing sales). Own class, own database: one test moves the schema.</summary>
[Trait("Category", "Integration")]
public sealed class OfflinePersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private async Task<Guid> NewOrganizationAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Currency.Create("GMD"), "UTC", Now));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<OfflineGrant> SaveGrantAsync(Guid organization, Guid user, DateTimeOffset? superseded = null)
    {
        var grant = OfflineGrant.Issue(Guid.CreateVersion7(), organization, user, Now, Now.AddDays(30));
        if (superseded is { } at)
        {
            grant.Supersede(at);
        }

        await using var db = database.CreateContext(organization);
        db.Set<OfflineGrant>().Add(grant);
        db.Set<OfflineContact>().Add(new OfflineContact(Guid.CreateVersion7(), organization, grant.Id, Now));
        await db.SaveChangesAsync();
        return grant;
    }

    [Fact]
    public async Task AUser_CanHaveOnlyOneActiveGrant_ButManySupersededOnes_AndOtherUsersTheirOwn()
    {
        var organization = await NewOrganizationAsync();
        var user = Guid.CreateVersion7();
        await SaveGrantAsync(organization, user, superseded: Now.AddHours(1));
        await SaveGrantAsync(organization, user, superseded: Now.AddHours(2));
        await SaveGrantAsync(organization, user);
        await SaveGrantAsync(organization, Guid.CreateVersion7());

        var second = await Assert.ThrowsAsync<DbUpdateException>(() => SaveGrantAsync(organization, user));

        var postgres = Assert.IsType<PostgresException>(second.InnerException);
        Assert.Equal(("23505", "IX_OfflineGrants_OneActivePerUser"), (postgres.SqlState, postgres.ConstraintName));
    }

    [Fact]
    public async Task TwoOrganizations_MayEachHaveTheSameUsersActiveGrant_AndNeitherSeesTheOther()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        var user = Guid.CreateVersion7();
        var mine = await SaveGrantAsync(first, user);
        await SaveGrantAsync(second, user);

        await using var db = database.CreateContext(first);

        Assert.Equal([mine.Id], await db.Set<OfflineGrant>().Select(g => g.Id).ToArrayAsync());
        Assert.Equal(1, await db.Set<OfflineContact>().CountAsync());
    }

    [Fact]
    public async Task TheDatabase_RejectsAnExpiryThatIsNotAfterIssue_AndATimeBeforeIssue()
    {
        var organization = await NewOrganizationAsync();
        var grant = await SaveGrantAsync(organization, Guid.CreateVersion7());
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        foreach (var (set, constraint) in new[]
        {
            ("\"AbsoluteExpiresAtUtc\" = \"IssuedAtUtc\"", "CK_OfflineGrants_Expiry_AfterIssue"),
            ("\"SupersededAtUtc\" = \"IssuedAtUtc\" - interval '1 second'", "CK_OfflineGrants_Superseded_NotBeforeIssue"),
            ("\"RevokedAtUtc\" = \"IssuedAtUtc\" - interval '1 second'", "CK_OfflineGrants_Revoked_NotBeforeIssue"),
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE \"OfflineGrants\" SET {set} WHERE \"Id\" = @id";
            command.Parameters.AddWithValue("id", grant.Id);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(("23514", constraint), (exception.SqlState, exception.ConstraintName));
        }
    }

    [Fact]
    public async Task AContact_CannotBelongToAnotherOrganizationsGrant()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        var grant = await SaveGrantAsync(first, Guid.CreateVersion7());

        await using var db = database.CreateContext(second);
        db.Set<OfflineContact>().Add(new OfflineContact(Guid.CreateVersion7(), second, grant.Id, Now));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23503", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task Contacts_AreAppendOnly_ForTheApplication()
    {
        var organization = await NewOrganizationAsync();
        var grant = await SaveGrantAsync(organization, Guid.CreateVersion7());

        await using var db = database.CreateContext(organization);
        var contact = await db.Set<OfflineContact>().SingleAsync(c => c.GrantId == grant.Id);
        db.Set<OfflineContact>().Remove(contact);

        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AWriteForAnotherOrganization_IsRefused()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        var foreign = OfflineGrant.Issue(Guid.CreateVersion7(), second, Guid.CreateVersion7(), Now, Now.AddDays(1));

        await using var db = database.CreateContext(first);
        db.Set<OfflineGrant>().Add(foreign);

        await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TheMigration_BackfillsReceivedAtForExistingSales_AndRollsBack()
    {
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToArray();
        var offline = migrations.Single(name => name.EndsWith("_AddOfflineGrants", StringComparison.Ordinal));
        var before = migrations[Array.IndexOf(migrations, offline) - 1];
        var organization = await NewOrganizationAsync();
        var sale = Guid.CreateVersion7();
        var created = Now.AddDays(-3);

        await migrator.MigrateAsync(before);
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'Sales' AND column_name = 'ReceivedAtUtc'"));
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO \"Sales\" (\"Id\", \"OrganizationId\", \"Currency\", \"Status\", \"CreatedByUserId\", \"CreatedAtUtc\", \"PaymentRevision\") VALUES (@id, @org, 'GMD', 'Completed', @user, @created, 0)";
            insert.Parameters.AddWithValue("id", sale);
            insert.Parameters.AddWithValue("org", organization);
            insert.Parameters.AddWithValue("user", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("created", created);
            await insert.ExecuteNonQueryAsync();
        }

        await context.Database.MigrateAsync();

        await using var check = new NpgsqlConnection(database.ConnectionString);
        await check.OpenAsync();
        await using var select = check.CreateCommand();
        select.CommandText = "SELECT \"ReceivedAtUtc\", \"OfflineGrantId\" FROM \"Sales\" WHERE \"Id\" = @id";
        select.Parameters.AddWithValue("id", sale);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(created, reader.GetFieldValue<DateTimeOffset>(0));
        Assert.True(await reader.IsDBNullAsync(1));
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
