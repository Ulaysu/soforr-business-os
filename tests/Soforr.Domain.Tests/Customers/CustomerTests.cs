using Soforr.Domain.Common;
using Soforr.Domain.Customers;

namespace Soforr.Domain.Tests.Customers;

public class CustomerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Organization = Guid.CreateVersion7();

    private static Customer New(string? name = "  Awa Jallow  ", string? contact = "  +220 555 0100  ", int? terms = 30) =>
        Customer.Create(Guid.CreateVersion7(), Organization, name, contact, terms, Now);

    private static string CodeOf(Action action) => Assert.Throws<DomainException>(action).Code;

    // ---- creating

    [Fact]
    public void ACustomer_IsCreatedActive_WithTrimmedDetailsAndItsOrganization()
    {
        var customer = New();

        Assert.Equal(Organization, customer.OrganizationId);
        Assert.Equal("Awa Jallow", customer.Name);
        Assert.Equal("+220 555 0100", customer.ContactDetails);
        Assert.Equal(30, customer.CreditTermsDays);
        Assert.True(customer.IsActive);
        Assert.Equal((Now, Now), (customer.CreatedAtUtc, customer.UpdatedAtUtc));
    }

    [Fact]
    public void ContactDetailsAndCreditTerms_AreOptional()
    {
        var customer = New(contact: null, terms: null);
        var blank = New(contact: "   ", terms: null);

        Assert.Null(customer.ContactDetails);
        Assert.Null(customer.CreditTermsDays);
        Assert.Null(blank.ContactDetails);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameIsRequired(string? name)
    {
        Assert.Equal("customer.name_required", CodeOf(() => New(name: name)));
    }

    [Fact]
    public void TheLengthLimits_AreEnforced_ExactlyAtTheBoundary()
    {
        Assert.Equal("customer.name_too_long", CodeOf(() => New(name: new string('x', Customer.MaxNameLength + 1))));
        Assert.Equal("customer.contact_details_too_long", CodeOf(() => New(contact: new string('x', Customer.MaxContactDetailsLength + 1))));
        Assert.Equal(Customer.MaxNameLength, New(name: new string('x', Customer.MaxNameLength)).Name.Length);
        Assert.Equal(Customer.MaxContactDetailsLength, New(contact: new string('x', Customer.MaxContactDetailsLength)).ContactDetails!.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(365)]
    public void CreditTerms_AcceptZeroToAYear(int days)
    {
        Assert.Equal(days, New(terms: days).CreditTermsDays);
    }

    [Theory]
    [InlineData(-1, "customer.credit_terms_negative")]
    [InlineData(366, "customer.credit_terms_too_long")]
    [InlineData(int.MaxValue, "customer.credit_terms_too_long")]
    public void CreditTerms_OutsideTheRange_AreRejected(int days, string code)
    {
        Assert.Equal(code, CodeOf(() => New(terms: days)));
    }

    [Fact]
    public void EmptyIdentifiersAndNonUtcTimes_AreRejected()
    {
        Assert.Equal("customer.id_required", CodeOf(() => Customer.Create(Guid.Empty, Organization, "N", null, null, Now)));
        Assert.Equal("customer.organization_required", CodeOf(() => Customer.Create(Guid.CreateVersion7(), Guid.Empty, "N", null, null, Now)));
        Assert.Equal("customer.timestamp_not_utc", CodeOf(() => Customer.Create(Guid.CreateVersion7(), Organization, "N", null, null, new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(1)))));
    }

    // ---- updating

    [Fact]
    public void AnUpdate_ReplacesTheDetails_AndTouchesTheTime()
    {
        var customer = New();

        customer.Update("  Awa J.  ", null, null, Now.AddDays(1));

        Assert.Equal("Awa J.", customer.Name);
        Assert.Null(customer.ContactDetails);
        Assert.Null(customer.CreditTermsDays);
        Assert.Equal(Now, customer.CreatedAtUtc);
        Assert.Equal(Now.AddDays(1), customer.UpdatedAtUtc);
    }

    [Fact]
    public void ARejectedUpdate_ChangesNothing()
    {
        var customer = New();

        Assert.Equal("customer.credit_terms_too_long", CodeOf(() => customer.Update("New name", "new contact", 1000, Now.AddDays(1))));
        Assert.Equal("customer.name_required", CodeOf(() => customer.Update(" ", "new contact", 5, Now.AddDays(1))));
        Assert.Equal("customer.timestamp_not_utc", CodeOf(() => customer.Update("New name", "new contact", 5, new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.FromHours(2)))));

        Assert.Equal(("Awa Jallow", "+220 555 0100", 30, Now), (customer.Name, customer.ContactDetails, customer.CreditTermsDays, customer.UpdatedAtUtc));
    }

    // ---- deactivating

    [Fact]
    public void ACustomer_CanBeDeactivated_WithoutBeingDeleted()
    {
        var customer = New();

        customer.Deactivate(Now.AddDays(2));

        Assert.False(customer.IsActive);
        Assert.Equal(Now.AddDays(2), customer.UpdatedAtUtc);
        Assert.Equal("Awa Jallow", customer.Name);
    }

    [Fact]
    public void DeactivatingAnInactiveCustomer_ChangesNothing()
    {
        var customer = New();
        customer.Deactivate(Now.AddDays(2));

        customer.Deactivate(Now.AddDays(9));

        Assert.Equal(Now.AddDays(2), customer.UpdatedAtUtc);
    }

    [Fact]
    public void ADeactivatedCustomer_CanStillBeUpdated()
    {
        var customer = New();
        customer.Deactivate(Now.AddDays(1));

        customer.Update("Renamed", null, null, Now.AddDays(2));

        Assert.Equal("Renamed", customer.Name);
        Assert.False(customer.IsActive);
    }

    [Fact]
    public void DeactivatingWithANonUtcTime_IsRejected()
    {
        var customer = New();

        Assert.Equal("customer.timestamp_not_utc", CodeOf(() => customer.Deactivate(new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.FromHours(1)))));
        Assert.True(customer.IsActive);
    }
}
