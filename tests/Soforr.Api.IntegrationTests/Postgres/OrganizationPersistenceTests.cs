using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Soforr.Application.Persistence;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Organization persistence against real PostgreSQL. The constraint tests write raw SQL on purpose: they prove the
/// database rejects invalid data even when the Domain is bypassed.
/// </summary>
[Trait("Category", "Integration")]
public class OrganizationPersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Created = new(2026, 5, 4, 9, 30, 15, TimeSpan.Zero);

    private static Organization NewOrganization(Guid? id = null) =>
        Organization.Create(id ?? Guid.CreateVersion7(), "Soforr Trading", Currency.Create("GMD"), "Europe/London", Created);

    // ---- round trip

    [Fact]
    public async Task AnOrganization_IsStoredAndReadBack_WithEveryField()
    {
        var original = NewOrganization();
        await using (var writer = database.CreateContext())
        {
            writer.Set<Organization>().Add(original);
            await writer.SaveChangesAsync();
        }

        await using var reader = database.CreateContext();
        var loaded = await reader.Set<Organization>().SingleAsync(organization => organization.Id == original.Id);

        Assert.Equal("Soforr Trading", loaded.Name);
        Assert.Equal(Currency.Create("GMD"), loaded.Currency);
        Assert.Equal("Europe/London", loaded.TimeZoneId);
        Assert.Equal(Created, loaded.CreatedAtUtc);
        Assert.Equal(Created, loaded.UpdatedAtUtc);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAtUtc.Offset);
    }

    [Fact]
    public async Task ChangedSettings_ArePersisted_AndTheCurrencyAndCreationTimeStayTheSame()
    {
        var id = Guid.CreateVersion7();
        await using (var writer = database.CreateContext())
        {
            writer.Set<Organization>().Add(NewOrganization(id));
            await writer.SaveChangesAsync();
        }

        var later = Created.AddDays(2);
        await using (var updater = database.CreateContext())
        {
            var organization = await updater.Set<Organization>().SingleAsync(o => o.Id == id);
            organization.UpdateSettings("Soforr Group", "UTC", later);
            await updater.SaveChangesAsync();
        }

        await using var reader = database.CreateContext();
        var loaded = await reader.Set<Organization>().SingleAsync(o => o.Id == id);
        Assert.Equal("Soforr Group", loaded.Name);
        Assert.Equal("UTC", loaded.TimeZoneId);
        Assert.Equal(later, loaded.UpdatedAtUtc);
        Assert.Equal(Created, loaded.CreatedAtUtc);
        Assert.Equal(Currency.Create("GMD"), loaded.Currency);
    }

    [Fact]
    public async Task TheCurrency_IsStoredAsTheThreeLetterCode()
    {
        var organization = NewOrganization();
        await using (var writer = database.CreateContext())
        {
            writer.Set<Organization>().Add(organization);
            await writer.SaveChangesAsync();
        }

        var stored = await ScalarAsync("SELECT \"Currency\" FROM \"Organizations\" WHERE \"Id\" = @id", ("id", organization.Id));

        Assert.Equal("GMD", stored);
    }

    [Fact]
    public async Task TheOrganizationIsReachableThroughTheApplicationPort_InTheRealApplication()
    {
        using var factory = new ApiFactory(database.ConnectionString).WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        var organization = NewOrganization();

        using (var writeScope = factory.Services.CreateScope())
        {
            var db = writeScope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Organization>().Add(organization);
            await db.SaveChangesAsync();
        }

        using var readScope = factory.Services.CreateScope();
        var reader = readScope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var loaded = await reader.Set<Organization>().AsNoTracking().SingleAsync(o => o.Id == organization.Id);

        Assert.Equal(organization.Name, loaded.Name);
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
            command.CommandText = "SELECT column_name, data_type, character_maximum_length, is_nullable FROM information_schema.columns WHERE table_name = 'Organizations'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetString(3));
            }
        }

        Assert.Equal(("uuid", null, "NO"), columns["Id"]);
        Assert.Equal(("character varying", 200, "NO"), columns["Name"]);
        Assert.Equal(("character varying", 3, "NO"), columns["Currency"]);
        Assert.Equal(("character varying", 100, "NO"), columns["TimeZoneId"]);
        Assert.Equal(("timestamp with time zone", null, "NO"), columns["CreatedAtUtc"]);
        Assert.Equal(("timestamp with time zone", null, "NO"), columns["UpdatedAtUtc"]);
        Assert.Equal(6, columns.Count);
    }

    // ---- constraints enforced by the database itself

    [Theory]
    [InlineData("GM", "CK_Organizations_Currency_Format")]
    [InlineData("gmd", "CK_Organizations_Currency_Format")]
    [InlineData("G1D", "CK_Organizations_Currency_Format")]
    [InlineData("", "CK_Organizations_Currency_Format")]
    public async Task TheDatabase_RejectsAMalformedCurrency(string currency, string constraint)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), "Name", currency, "UTC"));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsACurrencyLongerThanThreeCharacters()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), "Name", "GMDX", "UTC"));

        Assert.Equal("22001", exception.SqlState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TheDatabase_RejectsABlankName(string name)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), name, "GMD", "UTC"));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("CK_Organizations_Name_NotBlank", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsANameLongerThanTheMaximum()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRawAsync(Guid.CreateVersion7(), new string('n', Organization.MaxNameLength + 1), "GMD", "UTC"));

        Assert.Equal("22001", exception.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RejectsABlankTimeZone()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), "Name", "GMD", "  "));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("CK_Organizations_TimeZoneId_NotBlank", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsMissingValues()
    {
        var noName = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), null, "GMD", "UTC"));
        var noCurrency = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), "Name", null, "UTC"));
        var noTimeZone = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), "Name", "GMD", null));

        Assert.Equal("23502", noName.SqlState);
        Assert.Equal("23502", noCurrency.SqlState);
        Assert.Equal("23502", noTimeZone.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RejectsADuplicateIdentifier()
    {
        var id = Guid.CreateVersion7();
        await InsertRawAsync(id, "First", "GMD", "UTC");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(id, "Second", "GMD", "UTC"));

        Assert.Equal("23505", exception.SqlState);
    }

    [Fact]
    public async Task AValidRowWrittenWithRawSql_IsAccepted_AndReadableThroughTheDomainModel()
    {
        var id = Guid.CreateVersion7();
        await InsertRawAsync(id, "Raw Insert Ltd", "EUR", "Europe/London");

        await using var reader = database.CreateContext();
        var loaded = await reader.Set<Organization>().SingleAsync(o => o.Id == id);

        Assert.Equal("Raw Insert Ltd", loaded.Name);
        Assert.Equal(Currency.Create("EUR"), loaded.Currency);
    }

    private async Task InsertRawAsync(Guid id, string? name, string? currency, string? timeZoneId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO \"Organizations\" (\"Id\", \"Name\", \"Currency\", \"TimeZoneId\", \"CreatedAtUtc\", \"UpdatedAtUtc\") " +
            "VALUES (@id, @name, @currency, @timeZoneId, @created, @created)";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("currency", (object?)currency ?? DBNull.Value);
        command.Parameters.AddWithValue("timeZoneId", (object?)timeZoneId ?? DBNull.Value);
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
