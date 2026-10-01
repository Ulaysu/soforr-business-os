using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Organizations.GetOrganization;
using Soforr.Application.Organizations.UpdateOrganizationSettings;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;

namespace Soforr.Api.IntegrationTests.Organizations;

/// <summary>
/// The Organization handlers on real PostgreSQL (ADR 0005: handler tests use the real database where LINQ and
/// persistence matter), with a fixed clock and a stand-in caller so results are deterministic.
/// </summary>
[Trait("Category", "Integration")]
public class OrganizationHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;

        public static FakeUser In(Guid organizationId, Role role) => new(Guid.CreateVersion7(), organizationId, role);
    }

    private async Task<Guid> CreateAsync(string name = "Soforr Trading", string currency = "GMD", string timeZoneId = "Europe/London")
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, name, Currency.Create(currency), timeZoneId, Now));
        await db.SaveChangesAsync();
        return id;
    }

    // ---- update

    [Fact]
    public async Task Update_ByTheOwner_ChangesNameAndTimeZone_AndKeepsCurrencyAndCreationTime()
    {
        var id = await CreateAsync();
        var later = Now.AddDays(3);
        await using var db = database.CreateContext(id);

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(later), FakeUser.In(id, Role.Owner))
            .HandleAsync(new UpdateOrganizationSettingsCommand(id, "Soforr Group", "UTC"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Soforr Group", result.Value.Name);
        Assert.Equal("UTC", result.Value.TimeZoneId);
        Assert.Equal("GMD", result.Value.Currency);
        Assert.Equal(Now, result.Value.CreatedAtUtc);
        Assert.Equal(later, result.Value.UpdatedAtUtc);

        await using var verify = database.CreateContext(id);
        var stored = await verify.Set<Organization>().SingleAsync(o => o.Id == id);
        Assert.Equal("Soforr Group", stored.Name);
        Assert.Equal(later, stored.UpdatedAtUtc);
    }

    [Theory]
    [InlineData("", "UTC", "organization.name_required", "name")]
    [InlineData("New name", "Nowhere/Land", "organization.timezone_invalid", "timeZoneId")]
    public async Task Update_WithAnInvalidValue_ReturnsAValidationError_AndChangesNothingInTheDatabase(
        string name,
        string timeZoneId,
        string expectedCode,
        string expectedField)
    {
        var id = await CreateAsync();
        await using var db = database.CreateContext(id);

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(id, Role.Owner))
            .HandleAsync(new UpdateOrganizationSettingsCommand(id, name, timeZoneId), CancellationToken.None);

        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(expectedField, result.Error.Field);

        await using var verify = database.CreateContext(id);
        var stored = await verify.Set<Organization>().SingleAsync(o => o.Id == id);
        Assert.Equal("Soforr Trading", stored.Name);
        Assert.Equal("Europe/London", stored.TimeZoneId);
        Assert.Equal(Now, stored.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(Role.Manager)]
    [InlineData(Role.Staff)]
    public async Task Update_WithoutThePermission_IsForbidden_AndChangesNothing(Role role)
    {
        var id = await CreateAsync();
        await using var db = database.CreateContext(id);

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(id, role))
            .HandleAsync(new UpdateOrganizationSettingsCommand(id, "Hijacked", "UTC"), CancellationToken.None);

        Assert.Equal("identity.forbidden", result.Error!.Code);
        Assert.Equal(ErrorKind.Forbidden, result.Error.Kind);
        await using var verify = database.CreateContext(id);
        Assert.Equal("Soforr Trading", (await verify.Set<Organization>().SingleAsync(o => o.Id == id)).Name);
    }

    [Fact]
    public async Task Update_WithoutBeingSignedIn_IsNotAuthenticated()
    {
        var id = await CreateAsync();
        await using var db = database.CreateContext(id);

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(Now), new FakeUser(null, null, null))
            .HandleAsync(new UpdateOrganizationSettingsCommand(id, "Name", "UTC"), CancellationToken.None);

        Assert.Equal("identity.not_authenticated", result.Error!.Code);
        Assert.Equal(ErrorKind.Unauthenticated, result.Error.Kind);
    }

    [Fact]
    public async Task Update_WithoutAMembership_IsForbidden()
    {
        var id = await CreateAsync();
        await using var db = database.CreateContext(id);

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(Now), new FakeUser(Guid.CreateVersion7(), null, null))
            .HandleAsync(new UpdateOrganizationSettingsCommand(id, "Name", "UTC"), CancellationToken.None);

        Assert.Equal("identity.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task Update_OfAnotherOrganization_IsNotFound_AndChangesNothing()
    {
        var mine = await CreateAsync("Mine");
        var theirs = await CreateAsync("Theirs");
        await using var db = database.CreateContext(mine);

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(mine, Role.Owner))
            .HandleAsync(new UpdateOrganizationSettingsCommand(theirs, "Hijacked", "UTC"), CancellationToken.None);

        Assert.Equal("organization.not_found", result.Error!.Code);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        await using var verify = database.CreateContext(theirs);
        Assert.Equal("Theirs", (await verify.Set<Organization>().SingleAsync(o => o.Id == theirs)).Name);
    }

    [Fact]
    public async Task Update_OfAnOrganizationThatDoesNotExist_IsNotFound()
    {
        var missing = Guid.CreateVersion7();
        await using var db = database.CreateContext(missing);

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(Now), FakeUser.In(missing, Role.Owner))
            .HandleAsync(new UpdateOrganizationSettingsCommand(missing, "Name", "UTC"), CancellationToken.None);

        Assert.Equal("organization.not_found", result.Error!.Code);
    }

    // ---- get

    [Theory]
    [InlineData(Role.Owner)]
    [InlineData(Role.Manager)]
    [InlineData(Role.Staff)]
    public async Task Get_ReturnsTheCallersOrganization_ToEveryRole(Role role)
    {
        var id = await CreateAsync("Get Me Ltd", "EUR", "Europe/London");
        await using var db = database.CreateContext(id);

        var result = await new GetOrganizationHandler(db, FakeUser.In(id, role)).HandleAsync(new GetOrganizationQuery(id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Get Me Ltd", result.Value.Name);
        Assert.Equal("EUR", result.Value.Currency);
        Assert.Equal(Now, result.Value.CreatedAtUtc);
    }

    [Fact]
    public async Task Get_WithoutBeingSignedIn_IsNotAuthenticated()
    {
        var id = await CreateAsync();
        await using var db = database.CreateContext(id);

        var result = await new GetOrganizationHandler(db, new FakeUser(null, null, null)).HandleAsync(new GetOrganizationQuery(id), CancellationToken.None);

        Assert.Equal("identity.not_authenticated", result.Error!.Code);
    }

    [Fact]
    public async Task Get_OfAnotherOrganization_IsNotFound_NotForbidden()
    {
        var mine = await CreateAsync("Mine");
        var theirs = await CreateAsync("Theirs");
        await using var db = database.CreateContext(mine);

        var result = await new GetOrganizationHandler(db, FakeUser.In(mine, Role.Owner)).HandleAsync(new GetOrganizationQuery(theirs), CancellationToken.None);

        Assert.Equal("organization.not_found", result.Error!.Code);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }
}
