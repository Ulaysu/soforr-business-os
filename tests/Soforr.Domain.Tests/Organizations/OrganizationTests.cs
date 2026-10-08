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

        organization.UpdateSettings("  Soforr Group ", "Europe/London", null, later);

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

        var exception = Assert.Throws<DomainException>(() => organization.UpdateSettings(name, timeZoneId, null, CreatedAt.AddDays(1)));

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
            () => organization.UpdateSettings("New name", "Europe/London", null, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(2))));

        Assert.Equal("organization.timestamp_not_utc", exception.Code);
        Assert.Equal("Soforr", organization.Name);
    }

    [Theory]
    [InlineData("Etc/GMT+1")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("Asia/Kolkata")]
    public void Create_AcceptsOtherCanonicalIanaIdentifiers(string timeZoneId)
    {
        Assert.Equal(timeZoneId, Organization.Create(SomeId, "Soforr", Gmd, timeZoneId, CreatedAt).TimeZoneId);
    }

    [Theory]
    [InlineData("GMT Standard Time")]
    [InlineData("Pacific Standard Time")]
    [InlineData("Eastern Standard Time")]
    public void Create_RejectsWindowsTimeZoneIdentifiers_SoStoredValuesAreIana(string timeZoneId)
    {
        var exception = Assert.Throws<DomainException>(() => Organization.Create(SomeId, "Soforr", Gmd, timeZoneId, CreatedAt));

        Assert.Equal("organization.timezone_invalid", exception.Code);
    }

    [Fact]
    public void Create_RejectsNonCanonicalCasing_EvenAfterTheZoneHasBeenLoaded()
    {
        // The runtime matches case-insensitively once a zone is cached; validation must not depend on that.
        _ = Organization.Create(SomeId, "Soforr", Gmd, "Europe/London", CreatedAt);

        foreach (var variant in new[] { "europe/london", "EUROPE/LONDON", "utc" })
        {
            var exception = Assert.Throws<DomainException>(() => Organization.Create(SomeId, "Soforr", Gmd, variant, CreatedAt));
            Assert.Equal("organization.timezone_invalid", exception.Code);
        }
    }

    [Theory]
    [InlineData("../Europe/London")]
    [InlineData("Europe/../Europe/London")]
    [InlineData("../../etc/hostname")]
    [InlineData("/Europe/London")]
    [InlineData("Europe/London/")]
    [InlineData("Europe//London")]
    [InlineData("Europe\\London")]
    [InlineData("Europe/London\0")]
    [InlineData("Europe/Lon don")]
    public void Create_RejectsPathLikeAndMalformedTimeZoneIdentifiers(string timeZoneId)
    {
        var exception = Assert.Throws<DomainException>(() => Organization.Create(SomeId, "Soforr", Gmd, timeZoneId, CreatedAt));

        Assert.Equal("organization.timezone_invalid", exception.Code);
    }

    [Fact]
    public void UpdateSettings_RejectsAWindowsTimeZoneIdentifier_AndChangesNothing()
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt);

        Assert.Throws<DomainException>(() => organization.UpdateSettings("New name", "GMT Standard Time", null, CreatedAt.AddDays(1)));

        Assert.Equal("Soforr", organization.Name);
        Assert.Equal("UTC", organization.TimeZoneId);
    }

    [Fact]
    public void Create_RequiresAUtcTimestamp_ForBothCreatedAndUpdated()
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt);

        Assert.Equal(TimeSpan.Zero, organization.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, organization.UpdatedAtUtc.Offset);
    }

    // ---- the overdue materiality threshold

    [Fact]
    public void ANewOrganization_HasNoOverdueThreshold_SoNoMinimumIsAssumed()
    {
        Assert.Null(Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt).OverdueMaterialityThreshold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(1234.5678)]
    public void UpdateSettings_StoresAThreshold_InTheOrganizationsCurrency(double amount)
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt);

        organization.UpdateSettings("Soforr", "UTC", (decimal)amount, CreatedAt.AddDays(1));

        Assert.Equal((decimal)amount, organization.OverdueMaterialityThreshold);
    }

    [Fact]
    public void UpdateSettings_WithoutAThreshold_ClearsIt()
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt);
        organization.UpdateSettings("Soforr", "UTC", 500m, CreatedAt.AddDays(1));

        organization.UpdateSettings("Soforr", "UTC", null, CreatedAt.AddDays(2));

        Assert.Null(organization.OverdueMaterialityThreshold);
    }

    [Theory]
    [InlineData(-0.0001, "organization.overdue_threshold_negative")]
    [InlineData(-1, "organization.overdue_threshold_negative")]
    [InlineData(1.00001, "money.scale_exceeded")]
    public void UpdateSettings_WithAnInvalidThreshold_ChangesNothing(double amount, string expectedCode)
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt);
        organization.UpdateSettings("Soforr", "UTC", 500m, CreatedAt.AddDays(1));

        var exception = Assert.Throws<DomainException>(() => organization.UpdateSettings("Renamed", "Europe/London", (decimal)amount, CreatedAt.AddDays(2)));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(("Soforr", "UTC", 500m, CreatedAt.AddDays(1)), (organization.Name, organization.TimeZoneId, organization.OverdueMaterialityThreshold, organization.UpdatedAtUtc));
    }

    [Fact]
    public void AThresholdTooLargeToStore_IsRefused()
    {
        var organization = Organization.Create(SomeId, "Soforr", Gmd, "UTC", CreatedAt);

        var exception = Assert.Throws<DomainException>(() => organization.UpdateSettings("Soforr", "UTC", Money.MaxAmount + 1m, CreatedAt.AddDays(1)));

        Assert.Equal("money.amount_out_of_range", exception.Code);
        Assert.Null(organization.OverdueMaterialityThreshold);
    }
}
