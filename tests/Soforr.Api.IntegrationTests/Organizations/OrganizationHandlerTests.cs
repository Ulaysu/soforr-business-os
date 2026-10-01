using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Organizations.CreateOrganization;
using Soforr.Application.Organizations.GetOrganization;
using Soforr.Application.Organizations.UpdateOrganizationSettings;
using Soforr.Domain.Organizations;

namespace Soforr.Api.IntegrationTests.Organizations;

/// <summary>
/// The Organization handlers on real PostgreSQL (ADR 0005: handler tests use the real database where LINQ and
/// persistence matter), with a fixed clock and predictable ids so results are deterministic.
/// </summary>
[Trait("Category", "Integration")]
public class OrganizationHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FixedIds(Guid id) : IIdGenerator
    {
        public Guid NewId() => id;
    }

    private async Task<Guid> CreateAsync(string name = "Soforr Trading", string currency = "GMD", string timeZoneId = "Europe/London")
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        var result = await new CreateOrganizationHandler(db, new FixedClock(Now), new FixedIds(id))
            .HandleAsync(new CreateOrganizationCommand(name, currency, timeZoneId), CancellationToken.None);
        Assert.True(result.IsSuccess);
        return id;
    }

    // ---- create

    [Fact]
    public async Task Create_StoresTheOrganization_AndReturnsItWithTheGeneratedIdAndTheClockTime()
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();

        var result = await new CreateOrganizationHandler(db, new FixedClock(Now), new FixedIds(id))
            .HandleAsync(new CreateOrganizationCommand("  Soforr Trading ", "gmd", "Europe/London"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(id, result.Value.Id);
        Assert.Equal("Soforr Trading", result.Value.Name);
        Assert.Equal("GMD", result.Value.Currency);
        Assert.Equal("Europe/London", result.Value.TimeZoneId);
        Assert.Equal(Now, result.Value.CreatedAtUtc);
        Assert.Equal(Now, result.Value.UpdatedAtUtc);

        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Set<Organization>().CountAsync(o => o.Id == id));
    }

    [Theory]
    [InlineData(null, "GMD", "UTC", "organization.name_required", "name")]
    [InlineData("   ", "GMD", "UTC", "organization.name_required", "name")]
    [InlineData("Name", null, "UTC", "currency.invalid", "currency")]
    [InlineData("Name", "XXX", "UTC", "currency.invalid", "currency")]
    [InlineData("Name", "GMD", null, "organization.timezone_invalid", "timeZoneId")]
    [InlineData("Name", "GMD", "Mars/Olympus_Mons", "organization.timezone_invalid", "timeZoneId")]
    [InlineData("Name", "GMD", "GMT Standard Time", "organization.timezone_invalid", "timeZoneId")]
    public async Task Create_ReturnsAValidationError_NamingTheField_AndStoresNothing(
        string? name,
        string? currency,
        string? timeZoneId,
        string expectedCode,
        string expectedField)
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();

        var result = await new CreateOrganizationHandler(db, new FixedClock(Now), new FixedIds(id))
            .HandleAsync(new CreateOrganizationCommand(name, currency, timeZoneId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal(expectedField, result.Error.Field);

        await using var verify = database.CreateContext();
        Assert.Equal(0, await verify.Set<Organization>().CountAsync(o => o.Id == id));
    }

    [Fact]
    public async Task Create_RejectsANameThatIsTooLong()
    {
        await using var db = database.CreateContext();

        var result = await new CreateOrganizationHandler(db, new FixedClock(Now), new FixedIds(Guid.CreateVersion7()))
            .HandleAsync(new CreateOrganizationCommand(new string('n', Organization.MaxNameLength + 1), "GMD", "UTC"), CancellationToken.None);

        Assert.Equal("organization.name_too_long", result.Error!.Code);
        Assert.Equal("name", result.Error.Field);
    }

    // ---- update

    [Fact]
    public async Task Update_ChangesNameAndTimeZone_AndKeepsCurrencyAndCreationTime()
    {
        var id = await CreateAsync();
        var later = Now.AddDays(3);
        await using var db = database.CreateContext();

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(later))
            .HandleAsync(new UpdateOrganizationSettingsCommand(id, "Soforr Group", "UTC"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Soforr Group", result.Value.Name);
        Assert.Equal("UTC", result.Value.TimeZoneId);
        Assert.Equal("GMD", result.Value.Currency);
        Assert.Equal(Now, result.Value.CreatedAtUtc);
        Assert.Equal(later, result.Value.UpdatedAtUtc);

        await using var verify = database.CreateContext();
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
        await using var db = database.CreateContext();

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(Now.AddDays(1)))
            .HandleAsync(new UpdateOrganizationSettingsCommand(id, name, timeZoneId), CancellationToken.None);

        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(expectedField, result.Error.Field);

        await using var verify = database.CreateContext();
        var stored = await verify.Set<Organization>().SingleAsync(o => o.Id == id);
        Assert.Equal("Soforr Trading", stored.Name);
        Assert.Equal("Europe/London", stored.TimeZoneId);
        Assert.Equal(Now, stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task Update_OfAnUnknownOrganization_ReturnsNotFound()
    {
        await using var db = database.CreateContext();

        var result = await new UpdateOrganizationSettingsHandler(db, new FixedClock(Now))
            .HandleAsync(new UpdateOrganizationSettingsCommand(Guid.CreateVersion7(), "Name", "UTC"), CancellationToken.None);

        Assert.Equal("organization.not_found", result.Error!.Code);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    // ---- get

    [Fact]
    public async Task Get_ReturnsTheStoredOrganization()
    {
        var id = await CreateAsync("Get Me Ltd", "EUR", "Europe/London");
        await using var db = database.CreateContext();

        var result = await new GetOrganizationHandler(db).HandleAsync(new GetOrganizationQuery(id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Get Me Ltd", result.Value.Name);
        Assert.Equal("EUR", result.Value.Currency);
        Assert.Equal(Now, result.Value.CreatedAtUtc);
    }

    [Fact]
    public async Task Get_OfAnUnknownOrganization_ReturnsNotFound()
    {
        await using var db = database.CreateContext();

        var result = await new GetOrganizationHandler(db).HandleAsync(new GetOrganizationQuery(Guid.CreateVersion7()), CancellationToken.None);

        Assert.Equal("organization.not_found", result.Error!.Code);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }
}
