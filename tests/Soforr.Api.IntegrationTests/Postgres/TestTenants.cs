namespace Soforr.Api.IntegrationTests.Postgres;

/// <summary>
/// Stub of the two-tenant fixture that every tenant-isolation test will use (ADR 0005). It only provides two
/// fixed, distinct organization ids so tests can already be written against "tenant A" and "tenant B".
/// IDENTITY-003 completes it by creating both organizations and a signed-in user for each in the test database.
/// </summary>
public static class TestTenants
{
    public static Guid OrganizationA { get; } = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c0a01");

    public static Guid OrganizationB { get; } = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c0b02");
}
