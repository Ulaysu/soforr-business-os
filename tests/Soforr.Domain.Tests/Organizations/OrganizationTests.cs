using Soforr.Domain.Common;
using Soforr.Domain.Organizations;

namespace Soforr.Domain.Tests.Organizations;

public class OrganizationTests
{
    private static readonly Guid SomeId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a10");
    private static readonly Currency Gmd = Currency.Create("GMD");
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ProducesAnOrganizationWithTheGivenValues()
    {
        var organization = Organization.Create(SomeId, "Soforr Trading", Gmd, "Europe/London", CreatedAt);

        Assert.Equal(SomeId, organization.Id);
        Assert.Equal("Soforr Trading", organization.Name);
        Assert.Equal(Gmd, organization.Currency);
        Assert.Equal("Europe/London", organization.TimeZoneId);
        Assert.Equal(CreatedAt, organization.CreatedAtUtc);
        Assert.Equal(CreatedAt, organization.UpdatedAtUtc);
    }

    [Fact]
    public void Create_TrimsTheName()
    {
        var organization = Organization.Create(SomeId, "  Soforr Trading  ", Gmd, "UTC", CreatedAt);

        Assert.Equal("Soforr Trading", organization.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RequiresAName(string? name)
    {
        var exception = Assert.Throws<DomainException>(() => Organization.Create(SomeId, name, Gmd, "UTC", CreatedAt));

        Assert.Equal("organization.name_required", exception.Code);
    }

    [Fact]
    public void Create_AcceptsANameOfTheMaximumLength_AndRejectsOneCharacterMore()
    {
        var longestAllowed = new string('a', Organization.MaxNameLength);
        var tooLong = new string('a', Organization.MaxNameLength + 1);

        Assert.Equal(longestAllowed, Organization.Create(SomeId, longestAllowed, Gmd, "UTC", CreatedAt).Name);
        Assert.Equal(
            "organization.name_too_long",
            Assert.Throws<DomainException>(() => Organization.Create(SomeId, tooLong, Gmd, "UTC", CreatedAt)).Code);
    }

    [Fact]
    public void Create_RequiresAnIdentifier()
    {
        var exception = Assert.Throws<DomainException>(() => Organization.Create(Guid.Empty, "Soforr", Gmd, "UTC", CreatedAt));

        Assert.Equal("organization.id_required", exception.Code);
    }

    [Fact]
    public void Create_RequiresACurrency()
    {
        Assert.Throws<ArgumentNullException>(() => Organization.Create(SomeId, "Soforr", null!, "UTC", CreatedAt));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/London")]
    [InlineData("America/New_York")]
    public void Create_AcceptsIanaTimeZones(string timeZoneId)
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, timeZoneId, CreatedAt);

        Assert.Equal(timeZoneId, organization.TimeZoneId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("not a time zone")]
    public void Create_RejectsUnknownTimeZones(string? timeZoneId)
    {
        var exception = Assert.Throws<DomainException>(() => Organization.Create(SomeId, "Soforr", Gmd, timeZoneId, CreatedAt));

        Assert.Equal("organization.timezone_invalid", exception.Code);
    }

    [Fact]
    public void Create_RejectsTimestampsThatAreNotUtc()
    {
        var notUtc = new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.FromHours(1));

        var exception = Assert.Throws<DomainException>(() => Organization.Create(SomeId, "Soforr", Gmd, "UTC", notUtc));

        Assert.Equal("organization.timestamp_not_utc", exception.Code);
    }

    [Fact]
    public void UpdateSettings_ChangesNameAndTimeZone_AndRecordsWhenItHappened()
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt);
        var later = CreatedAt.AddDays(3);

        organization.UpdateSettings("  Soforr Group ", "Europe/London", later);

        Assert.Equal("Soforr Group", organization.Name);
        Assert.Equal("Europe/London", organization.TimeZoneId);
        Assert.Equal(later, organization.UpdatedAtUtc);
        Assert.Equal(CreatedAt, organization.CreatedAtUtc);
        Assert.Equal(Gmd, organization.Currency);
    }

    [Theory]
    [InlineData("", "Europe/London", "organization.name_required")]
    [InlineData("New name", "Nowhere/Land", "organization.timezone_invalid")]
    public void UpdateSettings_WithAnInvalidValue_ChangesNothing(string name, string timeZoneId, string expectedCode)
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt);

        var exception = Assert.Throws<DomainException>(() => organization.UpdateSettings(name, timeZoneId, CreatedAt.AddDays(1)));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal("Soforr", organization.Name);
        Assert.Equal("UTC", organization.TimeZoneId);
        Assert.Equal(CreatedAt, organization.UpdatedAtUtc);
    }

    [Fact]
    public void UpdateSettings_RejectsATimestampThatIsNotUtc_AndChangesNothing()
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt);

        var exception = Assert.Throws<DomainException>(
            () => organization.UpdateSettings("New name", "Europe/London", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(2))));

        Assert.Equal("organization.timestamp_not_utc", exception.Code);
        Assert.Equal("Soforr", organization.Name);
    }
}
