using Microsoft.AspNetCore.Mvc.Testing;
using Soforr.Api.IntegrationTests.Identity;

namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// The two-tenant fixture that every tenant-isolation test uses (ADR 0005): two organizations, each with an Owner
/// who is signed in through the real application with a client of their own. Tests act as tenant A and try to reach
/// tenant B's data, and the other way round. Create it with <see cref="CreateAsync"/> and dispose it.
/// </summary>
public sealed class TwoTenants : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    private TwoTenants(WebApplicationFactory<Program> factory, HttpClient clientA, HttpClient clientB, (Guid UserId, Guid OrganizationId, string Email) a, (Guid UserId, Guid OrganizationId, string Email) b)
    {
        _factory = factory;
        ClientA = clientA;
        ClientB = clientB;
        UserA = a.UserId;
        OrganizationA = a.OrganizationId;
        UserB = b.UserId;
        OrganizationB = b.OrganizationId;
    }

    /// <summary>The application both tenants use, so a test can add more clients (for example other roles).</summary>
    internal WebApplicationFactory<Program> Factory => _factory;

    /// <summary>A client signed in as the Owner of organization A.</summary>
    public HttpClient ClientA { get; }

    /// <summary>A client signed in as the Owner of organization B.</summary>
    public HttpClient ClientB { get; }

    public Guid OrganizationA { get; }

    public Guid OrganizationB { get; }

    public Guid UserA { get; }

    public Guid UserB { get; }

    public static async Task<TwoTenants> CreateAsync(PostgresDatabaseFixture database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var factory = AuthClient.CreateFactory(database.ConnectionString);
        var clientA = AuthClient.NewClient(factory);
        var clientB = AuthClient.NewClient(factory);
        var a = await AuthClient.RegisterAndSignInAsync(clientA, organizationName: "Tenant A");
        var b = await AuthClient.RegisterAndSignInAsync(clientB, organizationName: "Tenant B");

        return new TwoTenants(factory, clientA, clientB, a, b);
    }

    public void Dispose()
    {
        ClientA.Dispose();
        ClientB.Dispose();
        _factory.Dispose();
    }
}
