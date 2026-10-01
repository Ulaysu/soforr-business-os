using Testcontainers.PostgreSql;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// One PostgreSQL container for the whole test run (ADR 0005), started on first use and shared by every test class.
/// Test classes never use it directly: each gets its own database through <see cref="PostgresDatabaseFixture"/>.
/// The container is removed when the test process exits (and by Testcontainers' reaper if the process is killed).
/// The image matches the one in <c>docker-compose.yml</c>.
/// </summary>
internal static class SharedPostgresContainer
{
    private const string Image = "postgres:17";

    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private static PostgreSqlContainer? _container;

    /// <summary>A connection string to the container's administrative database, used to create and drop databases.</summary>
    public static async Task<string> GetAdministrationConnectionStringAsync()
    {
        await StartLock.WaitAsync();
        try
        {
            if (_container is null)
            {
                PostgreSqlContainer container;

                try
                {
                    container = new PostgreSqlBuilder(Image).Build();
                    await container.StartAsync();
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "Could not start the PostgreSQL test container. Integration tests need Docker to be running. " +
                        "To run only the tests that do not need it: dotnet test --filter \"Category!=Integration\".",
                        exception);
                }

                AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _container = container;
            }

            return _container.GetConnectionString();
        }
        finally
        {
            StartLock.Release();
        }
    }
}
