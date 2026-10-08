using Soforr.Domain.Common;
using Soforr.Domain.Sales;

namespace Soforr.Domain.Tests.Sales;

public class SaleOfflineProvenanceTests
{
    private static readonly Currency Gmd = Currency.Create("GMD");
    private static readonly DateTimeOffset Happened = new(2026, 6, 1, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Received = new(2026, 6, 1, 11, 3, 0, TimeSpan.Zero);

    private static SaleLineInput Line() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "SKU", "Rice", "bag", Quantity.Create(2m), Money.Create(10m, Gmd), Money.Create(10m, Gmd), Money.Create(6m, Gmd), null);

    [Fact]
    public void ASaleMadeOnline_WasReceivedWhenItHappened_AndHasNoGrant()
    {
        var sale = Sale.Complete(Guid.CreateVersion7(), Guid.CreateVersion7(), Gmd, Guid.CreateVersion7(), Happened, [Line()]);

        Assert.Equal((Happened, Happened, null), (sale.CreatedAtUtc, sale.ReceivedAtUtc, sale.OfflineGrantId));
    }

    [Fact]
    public void ASaleSynchronizedLater_KeepsWhenItHappened_WhenItWasReceived_AndTheGrantThatVouchedForIt()
    {
        var grant = Guid.CreateVersion7();

        var sale = Sale.Complete(Guid.CreateVersion7(), Guid.CreateVersion7(), Gmd, Guid.CreateVersion7(), Happened, [Line()], receivedAtUtc: Received, offlineGrantId: grant);

        Assert.Equal((Happened, Received, grant), (sale.CreatedAtUtc, sale.ReceivedAtUtc, sale.OfflineGrantId));
    }

    [Fact]
    public void TheReceiptTime_MustBeUtc_AndAnEmptyGrantIsRefused()
    {
        var organization = Guid.CreateVersion7();

        var offset = Assert.Throws<DomainException>(() => Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Guid.CreateVersion7(), Happened, [Line()], receivedAtUtc: new DateTimeOffset(Received.DateTime, TimeSpan.FromHours(1))));
        var empty = Assert.Throws<DomainException>(() => Sale.Complete(Guid.CreateVersion7(), organization, Gmd, Guid.CreateVersion7(), Happened, [Line()], offlineGrantId: Guid.Empty));

        Assert.Equal("sale.timestamp_not_utc", offset.Code);
        Assert.Equal("sale.offline_grant_invalid", empty.Code);
    }

    [Fact]
    public void TheDueDate_OfACreditSale_FollowsWhenItHappened()
    {
        var sale = Sale.Complete(Guid.CreateVersion7(), Guid.CreateVersion7(), Gmd, Guid.CreateVersion7(), Happened, [Line()], Guid.CreateVersion7(), Happened.AddDays(30), Money.Zero(Gmd), null, Received, Guid.CreateVersion7());

        Assert.Equal(Happened.AddDays(30), sale.DueAtUtc);
        Assert.Equal(Received, sale.ReceivedAtUtc);
    }
}
