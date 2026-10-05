using Soforr.Domain.Common;
using Soforr.Domain.Purchasing;

namespace Soforr.Domain.Tests.Purchasing;

public class PurchaseReceiptTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Order = Guid.CreateVersion7();
    private static readonly Guid LineA = Guid.CreateVersion7();
    private static readonly Guid LineB = Guid.CreateVersion7();
    private static readonly Guid ProductA = Guid.CreateVersion7();
    private static readonly Guid ProductB = Guid.CreateVersion7();

    private static PurchaseReceipt Receipt(params PurchaseReceiptEntry[] entries) =>
        PurchaseReceipt.Create(Guid.CreateVersion7(), Organization, Order, Guid.CreateVersion7(), Now, entries);

    private static PurchaseReceiptEntry EntryA(decimal quantity) => new(LineA, ProductA, Quantity.Create(quantity));

    private static PurchaseReceiptEntry EntryB(decimal quantity) => new(LineB, ProductB, Quantity.Create(quantity));

    private static ReceiptLine Line(Guid line, decimal quantity) => new(line, Quantity.Create(quantity));

    [Fact]
    public void ARequestOfTheSameOrderLineAndQuantity_IsTheSameReceipt()
    {
        var receipt = Receipt(EntryA(5m), EntryB(2m));

        Assert.True(receipt.IsSameAs(Order, [Line(LineA, 5m), Line(LineB, 2m)]));
        Assert.True(receipt.IsSameAs(Order, [Line(LineB, 2m), Line(LineA, 5m)]));
    }

    [Fact]
    public void QuantitiesAreComparedByValue_NotByScale()
    {
        Assert.True(Receipt(EntryA(5m)).IsSameAs(Order, [Line(LineA, 5.0000m)]));
    }

    [Fact]
    public void ADifferentQuantity_IsNotTheSameReceipt()
    {
        Assert.False(Receipt(EntryA(5m)).IsSameAs(Order, [Line(LineA, 8m)]));
    }

    [Fact]
    public void ADifferentLine_IsNotTheSameReceipt()
    {
        Assert.False(Receipt(EntryA(5m)).IsSameAs(Order, [Line(LineB, 5m)]));
    }

    [Fact]
    public void AnAdditionalLine_IsNotTheSameReceipt()
    {
        Assert.False(Receipt(EntryA(5m)).IsSameAs(Order, [Line(LineA, 5m), Line(LineB, 1m)]));
    }

    [Fact]
    public void AMissingLine_IsNotTheSameReceipt()
    {
        Assert.False(Receipt(EntryA(5m), EntryB(2m)).IsSameAs(Order, [Line(LineA, 5m)]));
    }

    [Fact]
    public void ALineNamedTwice_IsNotTheSameReceipt_EvenWhenTheCountMatches()
    {
        Assert.False(Receipt(EntryA(5m), EntryB(2m)).IsSameAs(Order, [Line(LineA, 5m), Line(LineA, 5m)]));
    }

    [Fact]
    public void ADifferentOrder_IsNotTheSameReceipt()
    {
        Assert.False(Receipt(EntryA(5m)).IsSameAs(Guid.CreateVersion7(), [Line(LineA, 5m)]));
    }

    [Fact]
    public void ACreatedReceipt_CarriesItsOrganizationOrderAndLines()
    {
        var receipt = Receipt(EntryA(5m));

        Assert.Equal(Organization, receipt.OrganizationId);
        Assert.Equal(Order, receipt.PurchaseOrderId);
        var line = Assert.Single(receipt.Lines);
        Assert.Equal((Organization, receipt.Id, LineA, ProductA, 5m), (line.OrganizationId, line.ReceiptId, line.LineId, line.ProductId, line.Quantity.Value));
    }

    [Fact]
    public void AnInvalidReceipt_IsRejected()
    {
        var user = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        PurchaseReceiptEntry[] one = [EntryA(1m)];

        Assert.Equal("purchase_receipt.id_required", Assert.Throws<DomainException>(() => PurchaseReceipt.Create(Guid.Empty, Organization, Order, user, Now, one)).Code);
        Assert.Equal("purchase_receipt.owner_required", Assert.Throws<DomainException>(() => PurchaseReceipt.Create(id, Guid.Empty, Order, user, Now, one)).Code);
        Assert.Equal("purchase_receipt.owner_required", Assert.Throws<DomainException>(() => PurchaseReceipt.Create(id, Organization, Guid.Empty, user, Now, one)).Code);
        Assert.Equal("purchase_receipt.owner_required", Assert.Throws<DomainException>(() => PurchaseReceipt.Create(id, Organization, Order, Guid.Empty, Now, one)).Code);
        Assert.Equal("purchase_receipt.timestamp_not_utc", Assert.Throws<DomainException>(() => PurchaseReceipt.Create(id, Organization, Order, user, new DateTimeOffset(Now.DateTime, TimeSpan.FromHours(1)), one)).Code);
        Assert.Equal("purchase_receipt.lines_required", Assert.Throws<DomainException>(() => PurchaseReceipt.Create(id, Organization, Order, user, Now, [])).Code);
        Assert.Equal("purchase_receipt.lines_invalid", Assert.Throws<DomainException>(() => PurchaseReceipt.Create(id, Organization, Order, user, Now, [EntryA(0m)])).Code);
        Assert.Equal("purchase_receipt.lines_invalid", Assert.Throws<DomainException>(() => PurchaseReceipt.Create(id, Organization, Order, user, Now, [EntryA(1m), EntryA(2m)])).Code);
    }
}
