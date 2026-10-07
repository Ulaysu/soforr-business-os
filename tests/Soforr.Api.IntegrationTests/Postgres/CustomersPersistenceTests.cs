using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Domain.Common;
using Soforr.Domain.Customers;
using Soforr.Domain.Organizations;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Customer persistence against real PostgreSQL: stored and read back with every field, updated and deactivated, and the
/// constraint tests write raw SQL on purpose, to prove the database refuses invalid data even when the Domain is bypassed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CustomersPersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 30, 15, TimeSpan.Zero);

    private async Task<Guid> NewOrganizationAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Currency.Create("GMD"), "UTC", Now));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> NewCustomerAsync(Guid organization, string name = "Awa Jallow", string? contact = "+220 555 0100", int? terms = 30)
    {
        var customer = Customer.Create(Guid.CreateVersion7(), organization, name, contact, terms, Now);
        await using var db = database.CreateContext(organization);
        db.Set<Customer>().Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    [Fact]
    public async Task ACustomer_IsStoredAndReadBack_WithEveryField()
    {
        var organization = await NewOrganizationAsync();
        var id = await NewCustomerAsync(organization);

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Customer>().AsNoTracking().SingleAsync(c => c.Id == id);

        Assert.Equal((organization, "Awa Jallow", "+220 555 0100", 30, true, Now, Now), (loaded.OrganizationId, loaded.Name, loaded.ContactDetails, loaded.CreditTermsDays, loaded.IsActive, loaded.CreatedAtUtc, loaded.UpdatedAtUtc));
    }

    [Fact]
    public async Task OptionalFields_AreStoredAsNull()
    {
        var organization = await NewOrganizationAsync();
        var id = await NewCustomerAsync(organization, contact: null, terms: null);

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Customer>().AsNoTracking().SingleAsync(c => c.Id == id);

        Assert.Null(loaded.ContactDetails);
        Assert.Null(loaded.CreditTermsDays);
    }

    [Fact]
    public async Task AnUpdateAndADeactivation_ArePersisted()
    {
        var organization = await NewOrganizationAsync();
        var id = await NewCustomerAsync(organization);

        await using (var db = database.CreateContext(organization))
        {
            var customer = await db.Set<Customer>().SingleAsync(c => c.Id == id);
            customer.Update("Renamed", null, 0, Now.AddDays(1));
            customer.Deactivate(Now.AddDays(2));
            await db.SaveChangesAsync();
        }

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<Customer>().AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(("Renamed", null, 0, false, Now.AddDays(2)), (loaded.Name, loaded.ContactDetails, loaded.CreditTermsDays, loaded.IsActive, loaded.UpdatedAtUtc));
    }

    // ---- constraints enforced by the database itself

    [Theory]
    [InlineData("  ", null, null, "CK_Customers_Name_NotBlank")]
    [InlineData("", null, null, "CK_Customers_Name_NotBlank")]
    [InlineData("N", " ", null, "CK_Customers_ContactDetails_NotBlank")]
    [InlineData("N", null, -1, "CK_Customers_CreditTermsDays_Range")]
    [InlineData("N", null, 366, "CK_Customers_CreditTermsDays_Range")]
    public async Task TheDatabase_RejectsInvalidCustomers(string name, string? contact, int? terms, string constraint)
    {
        var organization = await NewOrganizationAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, name, contact, terms));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(365)]
    [InlineData(null)]
    public async Task TheDatabase_AcceptsTheLimitsOfTheCreditTerms(int? terms)
    {
        var organization = await NewOrganizationAsync();

        await InsertRawAsync(organization, "N", null, terms);

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM \"Customers\" WHERE \"OrganizationId\" = '" + organization + "'"));
    }

    [Fact]
    public async Task TheDatabase_RefusesACustomerOfAnUnknownOrganization()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), "N", null, null));

        Assert.Equal("23503", exception.SqlState);
    }

    [Fact]
    public async Task TwoCustomersMayShareAName_BecausePeopleDo()
    {
        var organization = await NewOrganizationAsync();

        await NewCustomerAsync(organization, "Awa Jallow");
        await NewCustomerAsync(organization, "Awa Jallow");

        Assert.Equal(2, await CountAsync("SELECT count(*) FROM \"Customers\" WHERE \"OrganizationId\" = '" + organization + "'"));
    }

    [Fact]
    public async Task TheCustomerKey_ThatLetsSalesReferenceACustomerTogetherWithTheOrganization_Exists()
    {
        // CUSTOMER-002 makes sales reference (customer, organization), so the database guarantees they match.
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM pg_constraint WHERE conname = 'AK_Customers_Id_OrganizationId'"));
    }

    // ---- schema

    [Fact]
    public async Task TheTable_HasTheExpectedColumns_AndNoXmin()
    {
        var columns = new Dictionary<string, (string Type, string Nullable)>();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name = 'Customers'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
            }
        }

        Assert.Equal(("character varying", "NO"), columns["Name"]);
        Assert.Equal(("character varying", "YES"), columns["ContactDetails"]);
        Assert.Equal(("integer", "YES"), columns["CreditTermsDays"]);
        Assert.Equal(("boolean", "NO"), columns["IsActive"]);
        Assert.Equal(8, columns.Count);
    }

    private async Task InsertRawAsync(Guid organization, string name, string? contact, int? terms) =>
        await ExecuteAsync(
            "INSERT INTO \"Customers\" (\"Id\", \"OrganizationId\", \"Name\", \"ContactDetails\", \"CreditTermsDays\", \"IsActive\", \"CreatedAtUtc\", \"UpdatedAtUtc\") VALUES (@id, @org, @name, @contact, @terms, true, @now, @now)",
            ("id", Guid.CreateVersion7()),
            ("org", organization),
            ("name", name),
            ("contact", (object?)contact ?? DBNull.Value),
            ("terms", (object?)terms ?? DBNull.Value),
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

    private async Task<int> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
