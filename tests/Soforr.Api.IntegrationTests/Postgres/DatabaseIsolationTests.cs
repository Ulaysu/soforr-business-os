using Npgsql;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Proves that test classes do not share data. Two concrete classes run this same test in parallel; each writes a
/// row naming itself into a table with the same name and must then see only its own row. If the classes shared a
/// database, each would also see the other's row.
/// </summary>
[Trait("Category", "Integration")]
public abstract class DatabaseIsolationTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task EachClassSeesOnlyTheDataItWrote()
    {
        var owner = GetType().Name;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE isolation_marker (owner text NOT NULL)";
            await create.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO isolation_marker (owner) VALUES (@owner)";
            insert.Parameters.AddWithValue("owner", owner);
            await insert.ExecuteNonQueryAsync();
        }

        // Give the sibling class time to write its own row, so that a shared database would show both.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var owners = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT owner FROM isolation_marker";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                owners.Add(reader.GetString(0));
            }
        }

        Assert.Equal([owner], owners);
    }
}

public sealed class IsolationScenarioA(PostgresDatabaseFixture database) : DatabaseIsolationTests(database);

public sealed class IsolationScenarioB(PostgresDatabaseFixture database) : DatabaseIsolationTests(database);
