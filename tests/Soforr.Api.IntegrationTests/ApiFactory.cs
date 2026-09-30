using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Soforr.Api.IntegrationTests;

/// <summary>
/// Hosts the API for tests with a test-only database connection string that points at a closed port,
/// so tests that do not need a database never reach one. Real-PostgreSQL tests arrive with FOUNDATION-008.
/// </summary>
internal sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=test;Username=test;Password=test;Timeout=1;Command Timeout=1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = UnreachableConnectionString,
            }));
    }
}
