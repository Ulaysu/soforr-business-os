using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Soforr.Api.IntegrationTests;

/// <summary>
/// Hosts the real application for tests. By default the database connection string points at a closed port, so
/// tests that do not need a database never reach one. Pass the connection string of a
/// <see cref="Postgres.PostgresDatabaseFixture"/> to run against a real PostgreSQL database.
/// </summary>
internal sealed class ApiFactory(string? connectionString = null) : WebApplicationFactory<Program>
{
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=test;Username=test;Password=test;Timeout=1;Command Timeout=1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString ?? UnreachableConnectionString,
            }));
    }
}
