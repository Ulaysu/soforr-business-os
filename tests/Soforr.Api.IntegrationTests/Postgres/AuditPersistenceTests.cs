using Microsoft.EntityFrameworkCore;
using Npgsql;
using Soforr.Domain.Audit;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Audit event persistence against real PostgreSQL. The audit trail is append-only, and the tests prove it at every
/// level: the Domain has no way to change an event, the context refuses to save a change, and the database refuses
/// UPDATE, DELETE and TRUNCATE from raw SQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditPersistenceTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
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

    private static AuditEvent NewEvent(Guid organization, string action = "auth.sign_in_succeeded", Guid? actor = null, string? entityType = "User", Guid? entityId = null, string? description = null, string? correlationId = "corr-1") =>
        AuditEvent.Record(Guid.CreateVersion7(), organization, actor ?? Guid.CreateVersion7(), action, entityType, entityType is null ? null : entityId ?? Guid.CreateVersion7(), description, correlationId, Now);

    private async Task<AuditEvent> SaveAsync(Guid organization, AuditEvent auditEvent)
    {
        await using var db = database.CreateContext(organization);
        db.Set<AuditEvent>().Add(auditEvent);
        await db.SaveChangesAsync();
        return auditEvent;
    }

    // ---- round trip

    [Fact]
    public async Task AnEvent_IsStoredAndReadBack_WithEveryField()
    {
        var organization = await NewOrganizationAsync();
        var original = await SaveAsync(organization, NewEvent(organization, "membership.role_assigned", description: "Role Owner assigned on registration.", entityType: "Membership"));

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<AuditEvent>().SingleAsync(e => e.Id == original.Id);

        Assert.Equal(organization, loaded.OrganizationId);
        Assert.Equal(original.ActorId, loaded.ActorId);
        Assert.Equal("membership.role_assigned", loaded.Action);
        Assert.Equal("Membership", loaded.EntityType);
        Assert.Equal(original.EntityId, loaded.EntityId);
        Assert.Equal("Role Owner assigned on registration.", loaded.Description);
        Assert.Equal("corr-1", loaded.CorrelationId);
        Assert.Equal(Now, loaded.OccurredAtUtc);
    }

    [Fact]
    public async Task AnEventWithoutAnActorOrAReference_IsStored()
    {
        var organization = await NewOrganizationAsync();
        var system = AuditEvent.Record(Guid.CreateVersion7(), organization, null, "system.job_ran", null, null, null, null, Now);
        await SaveAsync(organization, system);

        await using var reader = database.CreateContext(organization);
        var loaded = await reader.Set<AuditEvent>().SingleAsync(e => e.Id == system.Id);

        Assert.Null(loaded.ActorId);
        Assert.Null(loaded.EntityType);
        Assert.Null(loaded.EntityId);
    }

    // ---- append-only: through the application

    [Fact]
    public async Task ARecordedEvent_CannotBeChanged_OrDeleted_ThroughTheApplication()
    {
        var organization = await NewOrganizationAsync();
        var original = await SaveAsync(organization, NewEvent(organization, description: "original"));

        await using (var changer = database.CreateContext(organization))
        {
            var loaded = await changer.Set<AuditEvent>().SingleAsync(e => e.Id == original.Id);
            changer.Entry(loaded).Property(e => e.Description).CurrentValue = "edited";
            await Assert.ThrowsAsync<AppendOnlyViolationException>(() => changer.SaveChangesAsync());
        }

        await using (var deleter = database.CreateContext(organization))
        {
            deleter.Set<AuditEvent>().Remove(await deleter.Set<AuditEvent>().SingleAsync(e => e.Id == original.Id));
            await Assert.ThrowsAsync<AppendOnlyViolationException>(() => deleter.SaveChangesAsync());
        }

        await using var verify = database.CreateContext(organization);
        Assert.Equal("original", (await verify.Set<AuditEvent>().SingleAsync(e => e.Id == original.Id)).Description);
    }

    [Fact]
    public async Task TheAppendOnlyRule_AppliesEvenWithoutAnOrganization()
    {
        var organization = await NewOrganizationAsync();
        var original = await SaveAsync(organization, NewEvent(organization));
        await using var nobody = database.CreateContext();
        nobody.Set<AuditEvent>().Attach(original).State = EntityState.Deleted;

        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => nobody.SaveChangesAsync());
    }

    // ---- append-only: in the database itself

    [Fact]
    public async Task TheDatabase_RefusesToUpdateARecordedEvent_EvenFromRawSql()
    {
        var organization = await NewOrganizationAsync();
        var original = await SaveAsync(organization, NewEvent(organization));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("UPDATE \"AuditEvents\" SET \"Description\" = 'tampered' WHERE \"Id\" = @id", ("id", original.Id)));

        Assert.Equal("23001", exception.SqlState);
        Assert.Contains("append-only", exception.MessageText, StringComparison.Ordinal);
        Assert.Null(await ScalarAsync("SELECT \"Description\" FROM \"AuditEvents\" WHERE \"Id\" = @id", original.Id));
    }

    [Fact]
    public async Task TheDatabase_RefusesToDeleteARecordedEvent_EvenFromRawSql()
    {
        var organization = await NewOrganizationAsync();
        var original = await SaveAsync(organization, NewEvent(organization));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("DELETE FROM \"AuditEvents\" WHERE \"Id\" = @id", ("id", original.Id)));

        Assert.Equal("23001", exception.SqlState);
        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM \"AuditEvents\" WHERE \"Id\" = @id", original.Id));
    }

    [Fact]
    public async Task TheDatabase_RefusesToTruncateTheTrail()
    {
        var organization = await NewOrganizationAsync();
        await SaveAsync(organization, NewEvent(organization));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("TRUNCATE TABLE \"AuditEvents\""));

        Assert.Equal("23001", exception.SqlState);
    }

    [Fact]
    public async Task TheDatabase_StillAcceptsNewEvents_SoOnlyChangesAreRefused()
    {
        var organization = await NewOrganizationAsync();

        await SaveAsync(organization, NewEvent(organization));
        await SaveAsync(organization, NewEvent(organization));

        await using var reader = database.CreateContext(organization);
        Assert.Equal(2, await reader.Set<AuditEvent>().CountAsync());
    }

    // ---- constraints enforced by the database itself

    [Theory]
    [InlineData("signin")]
    [InlineData("Auth.SignIn")]
    [InlineData("auth.")]
    [InlineData("auth..x")]
    [InlineData("auth.sign-in")]
    [InlineData("")]
    public async Task TheDatabase_RejectsAMalformedAction(string action)
    {
        var organization = await NewOrganizationAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, action));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("CK_AuditEvents_Action_Format", exception.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_AcceptsAWellFormedAction()
    {
        var organization = await NewOrganizationAsync();

        await InsertRawAsync(organization, "membership.role_assigned");

        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM \"AuditEvents\" WHERE \"OrganizationId\" = @id", organization));
    }

    [Fact]
    public async Task TheDatabase_RequiresTheEntityTypeAndIdTogether()
    {
        var organization = await NewOrganizationAsync();

        var typeOnly = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, "a.b", entityType: "User", entityId: null));
        var idOnly = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, "a.b", entityType: null, entityId: Guid.CreateVersion7()));

        Assert.Equal("CK_AuditEvents_Entity_BothOrNeither", typeOnly.ConstraintName);
        Assert.Equal("CK_AuditEvents_Entity_BothOrNeither", idOnly.ConstraintName);
    }

    [Fact]
    public async Task TheDatabase_RejectsABlankDescription_AndValuesThatAreTooLong()
    {
        var organization = await NewOrganizationAsync();

        var blank = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, "a.b", description: "   "));
        var longAction = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, "a." + new string('b', AuditEvent.MaxActionLength)));
        var longDescription = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, "a.b", description: new string('d', AuditEvent.MaxDescriptionLength + 1)));
        var longCorrelation = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(organization, "a.b", correlationId: new string('c', AuditEvent.MaxCorrelationIdLength + 1)));

        Assert.Equal("CK_AuditEvents_Description_NotBlank", blank.ConstraintName);
        Assert.All(new[] { longAction, longDescription, longCorrelation }, exception => Assert.Equal("22001", exception.SqlState));
    }

    [Fact]
    public async Task TheDatabase_RejectsAnEventForAnOrganizationThatDoesNotExist()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(Guid.CreateVersion7(), "a.b"));

        Assert.Equal("23503", exception.SqlState);
    }

    [Fact]
    public async Task TheDatabase_KeepsAnEvent_WhoseActorIsNotAUser()
    {
        var organization = await NewOrganizationAsync();

        // No foreign key to the user table: an event outlives any user, and system actors are not users.
        await InsertRawAsync(organization, "a.b", actor: Guid.CreateVersion7());

        Assert.Equal("1", await ScalarAsync("SELECT count(*)::text FROM \"AuditEvents\" WHERE \"OrganizationId\" = @id", organization));
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
            command.CommandText = "SELECT column_name, data_type, character_maximum_length, is_nullable FROM information_schema.columns WHERE table_name = 'AuditEvents'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetString(3));
            }
        }

        Assert.Equal(("uuid", null, "NO"), columns["Id"]);
        Assert.Equal(("uuid", null, "NO"), columns["OrganizationId"]);
        Assert.Equal(("uuid", null, "YES"), columns["ActorId"]);
        Assert.Equal(("character varying", 100, "NO"), columns["Action"]);
        Assert.Equal(("character varying", 50, "YES"), columns["EntityType"]);
        Assert.Equal(("uuid", null, "YES"), columns["EntityId"]);
        Assert.Equal(("character varying", 500, "YES"), columns["Description"]);
        Assert.Equal(("character varying", 64, "YES"), columns["CorrelationId"]);
        Assert.Equal(("timestamp with time zone", null, "NO"), columns["OccurredAtUtc"]);
        Assert.Equal(9, columns.Count);
    }

    // ---- raw helpers

    private async Task InsertRawAsync(Guid organization, string action, string? entityType = null, Guid? entityId = null, string? description = null, string? correlationId = null, Guid? actor = null) =>
        await ExecuteAsync(
            "INSERT INTO \"AuditEvents\" (\"Id\", \"OrganizationId\", \"ActorId\", \"Action\", \"EntityType\", \"EntityId\", \"Description\", \"CorrelationId\", \"OccurredAtUtc\") " +
            "VALUES (@id, @organization, @actor, @action, @entityType, @entityId, @description, @correlationId, @now)",
            ("id", Guid.CreateVersion7()),
            ("organization", organization),
            ("actor", (object?)actor ?? DBNull.Value),
            ("action", action),
            ("entityType", (object?)entityType ?? DBNull.Value),
            ("entityId", (object?)entityId ?? DBNull.Value),
            ("description", (object?)description ?? DBNull.Value),
            ("correlationId", (object?)correlationId ?? DBNull.Value),
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

    private async Task<string?> ScalarAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", id);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
