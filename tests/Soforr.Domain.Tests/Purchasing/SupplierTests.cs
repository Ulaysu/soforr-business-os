using Soforr.Domain.Common;
using Soforr.Domain.Purchasing;

namespace Soforr.Domain.Tests.Purchasing;

public class SupplierTests
{
    private static readonly Guid Id = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c6a01");
    private static readonly Guid OrganizationId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c6a02");
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static void AssertRejected(string code, Action action) => Assert.Equal(code, Assert.Throws<DomainException>(action).Code);

    [Fact]
    public void Create_KeepsTheTrimmedValues_AndStartsActive()
    {
        var supplier = Supplier.Create(Id, OrganizationId, "  Banjul Wholesale  ", "  +220 123 4567  ", Now);

        Assert.Equal("Banjul Wholesale", supplier.Name);
        Assert.Equal("+220 123 4567", supplier.ContactDetails);
        Assert.True(supplier.IsActive);
        Assert.Equal(Now, supplier.CreatedAtUtc);
        Assert.Equal(Now, supplier.UpdatedAtUtc);
    }

    [Fact]
    public void ContactDetailsAreOptional() => Assert.Null(Supplier.Create(Id, OrganizationId, "Name", "   ", Now).ContactDetails);

    [Fact]
    public void Create_RejectsInvalidValues()
    {
        AssertRejected("supplier.id_required", () => Supplier.Create(Guid.Empty, OrganizationId, "Name", null, Now));
        AssertRejected("supplier.organization_required", () => Supplier.Create(Id, Guid.Empty, "Name", null, Now));
        AssertRejected("supplier.name_required", () => Supplier.Create(Id, OrganizationId, "  ", null, Now));
        AssertRejected("supplier.name_required", () => Supplier.Create(Id, OrganizationId, null, null, Now));
        AssertRejected("supplier.name_too_long", () => Supplier.Create(Id, OrganizationId, new string('n', Supplier.MaxNameLength + 1), null, Now));
        AssertRejected("supplier.contact_details_too_long", () => Supplier.Create(Id, OrganizationId, "Name", new string('c', Supplier.MaxContactDetailsLength + 1), Now));
        AssertRejected("supplier.timestamp_not_utc", () => Supplier.Create(Id, OrganizationId, "Name", null, new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.FromHours(1))));
    }

    [Fact]
    public void Update_ReplacesTheDetails()
    {
        var supplier = Supplier.Create(Id, OrganizationId, "Old", "old", Now);

        supplier.Update("New", null, Now.AddDays(1));

        Assert.Equal("New", supplier.Name);
        Assert.Null(supplier.ContactDetails);
        Assert.Equal(Now.AddDays(1), supplier.UpdatedAtUtc);
        Assert.Equal(Now, supplier.CreatedAtUtc);
    }

    [Fact]
    public void ARejectedUpdate_LeavesTheSupplierUntouched()
    {
        var supplier = Supplier.Create(Id, OrganizationId, "Name", "contact", Now);

        AssertRejected("supplier.name_required", () => supplier.Update("", "new contact", Now.AddDays(1)));
        AssertRejected("supplier.contact_details_too_long", () => supplier.Update("New", new string('c', Supplier.MaxContactDetailsLength + 1), Now.AddDays(1)));

        Assert.Equal("Name", supplier.Name);
        Assert.Equal("contact", supplier.ContactDetails);
        Assert.Equal(Now, supplier.UpdatedAtUtc);
    }

    [Fact]
    public void Deactivate_TakesTheSupplierOutOfUse_AndRepeatingItChangesNothing()
    {
        var supplier = Supplier.Create(Id, OrganizationId, "Name", null, Now);

        supplier.Deactivate(Now.AddDays(1));
        supplier.Deactivate(Now.AddDays(2));

        Assert.False(supplier.IsActive);
        Assert.Equal(Now.AddDays(1), supplier.UpdatedAtUtc);
        AssertRejected("supplier.timestamp_not_utc", () => supplier.Deactivate(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(1))));
    }
}
