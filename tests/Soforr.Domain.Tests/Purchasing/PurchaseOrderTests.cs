using Soforr.Domain.Common;
using Soforr.Domain.Purchasing;

namespace Soforr.Domain.Tests.Purchasing;

public class PurchaseOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c5a01");
    private static readonly Guid OrganizationId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c5a02");
    private static readonly Guid SupplierId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c5a03");
    private static readonly Guid RiceId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c5b01");
    private static readonly Guid OilId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c5b02");
    private static readonly Guid RiceLine = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c5c01");
    private static readonly Guid OilLine = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c5c02");
    private static readonly Currency Gmd = Currency.Create("GMD");
    private static readonly Currency Eur = Currency.Create("EUR");
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static Money Gm(decimal amount) => Money.Create(amount, Gmd);

    private static Quantity Q(decimal value) => Quantity.Create(value);

    private static PurchaseOrder Draft(string? reference = null) => PurchaseOrder.CreateDraft(OrderId, OrganizationId, SupplierId, Gmd, reference, T0);

    private static PurchaseOrder DraftWithTwoLines()
    {
        var order = Draft();
        order.AddLine(RiceLine, RiceId, "RICE-25", "Rice 25kg", "bag", Q(10), Gm(90.5m), T0.AddMinutes(1));
        order.AddLine(OilLine, OilId, "OIL-5", "Oil 5l", "can", Q(4), Gm(300), T0.AddMinutes(2));
        return order;
    }

    private static PurchaseOrder Placed()
    {
        var order = DraftWithTwoLines();
        order.Place(T0.AddMinutes(10));
        return order;
    }

    private static void AssertRejected(string code, Action action)
    {
        var exception = Assert.Throws<DomainException>(action);
        Assert.Equal(code, exception.Code);
    }

    // ---- creating a draft

    [Fact]
    public void ANewOrder_IsADraft_WithNoLines_AndAZeroTotal()
    {
        var order = Draft("  Quote 42  ");

        Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
        Assert.Equal(OrderId, order.Id);
        Assert.Equal(OrganizationId, order.OrganizationId);
        Assert.Equal(SupplierId, order.SupplierId);
        Assert.Equal(Gmd, order.Currency);
        Assert.Equal("Quote 42", order.Reference);
        Assert.Empty(order.Lines);
        Assert.Equal(Money.Zero(Gmd), order.Total);
        Assert.Equal(T0, order.CreatedAtUtc);
        Assert.Equal(T0, order.UpdatedAtUtc);
        Assert.Null(order.PlacedAtUtc);
        Assert.Null(order.ClosedAtUtc);
    }

    [Fact]
    public void CreateDraft_RejectsMissingIdsAndAnInvalidReferenceOrTimestamp()
    {
        AssertRejected("purchase_order.id_required", () => PurchaseOrder.CreateDraft(Guid.Empty, OrganizationId, SupplierId, Gmd, null, T0));
        AssertRejected("purchase_order.organization_required", () => PurchaseOrder.CreateDraft(OrderId, Guid.Empty, SupplierId, Gmd, null, T0));
        AssertRejected("purchase_order.supplier_required", () => PurchaseOrder.CreateDraft(OrderId, OrganizationId, Guid.Empty, Gmd, null, T0));
        AssertRejected("purchase_order.reference_too_long", () => Draft(new string('r', PurchaseOrder.MaxReferenceLength + 1)));
        AssertRejected("purchase_order.timestamp_not_utc", () => PurchaseOrder.CreateDraft(OrderId, OrganizationId, SupplierId, Gmd, null, new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.FromHours(1))));
        Assert.Null(Draft("   ").Reference);
    }

    // ---- lines

    [Fact]
    public void AddingLines_SnapshotsTheProduct_AndComputesTheTotals()
    {
        var order = DraftWithTwoLines();

        var rice = order.Lines[0];
        Assert.Equal(RiceLine, rice.Id);
        Assert.Equal(OrganizationId, rice.OrganizationId);
        Assert.Equal(OrderId, rice.PurchaseOrderId);
        Assert.Equal("RICE-25", rice.SkuSnapshot);
        Assert.Equal("Rice 25kg", rice.NameSnapshot);
        Assert.Equal("bag", rice.UnitOfMeasureSnapshot);
        Assert.Equal(10m, rice.OrderedQuantity.Value);
        Assert.Equal(0m, rice.ReceivedQuantity.Value);
        Assert.Equal(10m, rice.OutstandingQuantity.Value);
        Assert.Equal(905m, rice.LineTotal.Amount);
        Assert.Equal(1200m, order.Lines[1].LineTotal.Amount);
        Assert.Equal(2105m, order.Total.Amount);
        Assert.Equal(T0.AddMinutes(2), order.UpdatedAtUtc);
    }

    [Fact]
    public void ALineTotal_IsRoundedToFourPlacesHalfAwayFromZero()
    {
        var order = Draft();
        order.AddLine(RiceLine, RiceId, "R", "Rice", "kg", Q(0.3333m), Money.Create(1.5m, Gmd), T0);

        // 0.3333 x 1.5 = 0.49995 exactly, which rounds up to 0.5000 (half away from zero).
        Assert.Equal(0.5m, order.Lines[0].LineTotal.Amount);
    }

    [Fact]
    public void AZeroUnitCost_IsAllowed_AndGivesAZeroLineTotal()
    {
        var order = Draft();

        order.AddLine(RiceLine, RiceId, "R", "Rice", "kg", Q(3), Gm(0), T0);

        Assert.Equal(0m, order.Total.Amount);
    }

    [Fact]
    public void AProductCanAppearOnlyOnce_OnAnOrder() =>
        AssertRejected("purchase_order.line_product_duplicate", () => DraftWithTwoLines().AddLine(Guid.NewGuid(), RiceId, "RICE-25", "Rice", "bag", Q(1), Gm(1), T0));

    [Theory]
    [InlineData("0", "1", "purchase_order.line_quantity_required")]
    [InlineData("1", "-0.0001", "purchase_order.line_unit_cost_negative")]
    public void InvalidLineValues_AreRejected_AndTheOrderIsUnchanged(string quantity, string cost, string code)
    {
        var order = Draft();

        AssertRejected(code, () => order.AddLine(RiceLine, RiceId, "R", "Rice", "kg", Q(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)), Gm(decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture)), T0));

        Assert.Empty(order.Lines);
        Assert.Equal(T0, order.UpdatedAtUtc);
    }

    [Fact]
    public void ALineMustBeInTheOrdersCurrency() =>
        AssertRejected("purchase_order.line_currency_mismatch", () => Draft().AddLine(RiceLine, RiceId, "R", "Rice", "kg", Q(1), Money.Create(1, Eur), T0));

    [Fact]
    public void LineIdentifiersAndSnapshotsAreRequired()
    {
        AssertRejected("purchase_order.line_id_required", () => Draft().AddLine(Guid.Empty, RiceId, "R", "Rice", "kg", Q(1), Gm(1), T0));
        AssertRejected("purchase_order.line_product_required", () => Draft().AddLine(RiceLine, Guid.Empty, "R", "Rice", "kg", Q(1), Gm(1), T0));
        AssertRejected("purchase_order.line_sku_required", () => Draft().AddLine(RiceLine, RiceId, " ", "Rice", "kg", Q(1), Gm(1), T0));
        AssertRejected("purchase_order.line_name_required", () => Draft().AddLine(RiceLine, RiceId, "R", null, "kg", Q(1), Gm(1), T0));
        AssertRejected("purchase_order.line_unit_of_measure_required", () => Draft().AddLine(RiceLine, RiceId, "R", "Rice", "", Q(1), Gm(1), T0));
        AssertRejected("purchase_order.line_sku_too_long", () => Draft().AddLine(RiceLine, RiceId, new string('s', PurchaseOrderLine.MaxSkuLength + 1), "Rice", "kg", Q(1), Gm(1), T0));
        AssertRejected("purchase_order.line_name_too_long", () => Draft().AddLine(RiceLine, RiceId, "R", new string('n', PurchaseOrderLine.MaxNameLength + 1), "kg", Q(1), Gm(1), T0));
        AssertRejected("purchase_order.line_unit_of_measure_too_long", () => Draft().AddLine(RiceLine, RiceId, "R", "Rice", new string('u', PurchaseOrderLine.MaxUnitOfMeasureLength + 1), Q(1), Gm(1), T0));
    }

    [Fact]
    public void ATotalThatCannotBeStored_IsRejected_AtTheLineThatCausesIt()
    {
        var order = Draft();
        order.AddLine(RiceLine, RiceId, "R", "Rice", "kg", Q(1), Money.Create(Money.MaxAmount, Gmd), T0);

        AssertRejected("money.amount_out_of_range", () => order.AddLine(OilLine, OilId, "O", "Oil", "l", Q(1), Gm(1), T0));

        Assert.Single(order.Lines);
        Assert.Equal(Money.MaxAmount, order.Total.Amount);
    }

    [Fact]
    public void UpdatingALine_ChangesItsQuantityAndCost_AndRecomputesTheTotal()
    {
        var order = DraftWithTwoLines();

        order.UpdateLine(RiceLine, Q(20), Gm(80), T0.AddMinutes(5));

        Assert.Equal(20m, order.Lines[0].OrderedQuantity.Value);
        Assert.Equal(80m, order.Lines[0].UnitCost.Amount);
        Assert.Equal(1600m + 1200m, order.Total.Amount);
        Assert.Equal(T0.AddMinutes(5), order.UpdatedAtUtc);
    }

    [Fact]
    public void ARejectedLineUpdate_LeavesTheOrderUntouched()
    {
        var order = Draft();
        order.AddLine(RiceLine, RiceId, "R", "Rice", "kg", Q(1), Gm(10), T0);
        order.AddLine(OilLine, OilId, "O", "Oil", "l", Q(1), Money.Create(Money.MaxAmount - 10, Gmd), T0);

        AssertRejected("money.amount_out_of_range", () => order.UpdateLine(RiceLine, Q(1), Gm(11), T0.AddMinutes(1)));
        AssertRejected("purchase_order.line_quantity_required", () => order.UpdateLine(RiceLine, Q(0), Gm(10), T0.AddMinutes(1)));
        AssertRejected("purchase_order.line_not_found", () => order.UpdateLine(Guid.NewGuid(), Q(1), Gm(1), T0));

        Assert.Equal(10m, order.Lines[0].UnitCost.Amount);
        Assert.Equal(1m, order.Lines[0].OrderedQuantity.Value);
        Assert.Equal(T0, order.UpdatedAtUtc);
    }

    [Fact]
    public void RemovingALine_RemovesItFromTheTotal()
    {
        var order = DraftWithTwoLines();

        order.RemoveLine(RiceLine, T0.AddMinutes(5));

        Assert.Equal([OilLine], order.Lines.Select(line => line.Id).ToArray());
        Assert.Equal(1200m, order.Total.Amount);
        AssertRejected("purchase_order.line_not_found", () => order.RemoveLine(RiceLine, T0.AddMinutes(6)));
    }

    [Fact]
    public void TheReferenceCanBeChanged_InADraft()
    {
        var order = Draft("old");

        order.SetReference("  new  ", T0.AddMinutes(1));

        Assert.Equal("new", order.Reference);
        AssertRejected("purchase_order.reference_too_long", () => order.SetReference(new string('r', PurchaseOrder.MaxReferenceLength + 1), T0.AddMinutes(2)));
        Assert.Equal("new", order.Reference);
    }

    // ---- placing

    [Fact]
    public void PlacingADraft_MakesItPlaced_AndFixesTheLines()
    {
        var order = DraftWithTwoLines();

        order.Place(T0.AddMinutes(10));

        Assert.Equal(PurchaseOrderStatus.Placed, order.Status);
        Assert.Equal(T0.AddMinutes(10), order.PlacedAtUtc);
        Assert.Equal(T0.AddMinutes(10), order.UpdatedAtUtc);
        Assert.Null(order.ClosedAtUtc);
        AssertRejected("purchase_order.not_draft", () => order.AddLine(Guid.NewGuid(), Guid.NewGuid(), "X", "X", "x", Q(1), Gm(1), T0));
        AssertRejected("purchase_order.not_draft", () => order.UpdateLine(RiceLine, Q(1), Gm(1), T0));
        AssertRejected("purchase_order.not_draft", () => order.RemoveLine(RiceLine, T0));
        AssertRejected("purchase_order.not_draft", () => order.SetReference("x", T0));
    }

    [Fact]
    public void AnOrderWithoutLines_CannotBePlaced()
    {
        var order = Draft();

        AssertRejected("purchase_order.no_lines", () => order.Place(T0.AddMinutes(1)));

        Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
    }

    [Fact]
    public void OnlyADraftCanBePlaced()
    {
        AssertRejected("purchase_order.cannot_place", () => Placed().Place(T0.AddMinutes(20)));

        var cancelled = DraftWithTwoLines();
        cancelled.Cancel(T0.AddMinutes(20));
        AssertRejected("purchase_order.cannot_place", () => cancelled.Place(T0.AddMinutes(30)));
    }

    // ---- receiving

    [Fact]
    public void AReceiptThatDoesNotCompleteEveryLine_MakesTheOrderPartiallyReceived()
    {
        var order = Placed();

        order.Receive([new ReceiptLine(RiceLine, Q(4))], T0.AddHours(1));

        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, order.Status);
        Assert.Equal(4m, order.Lines[0].ReceivedQuantity.Value);
        Assert.Equal(6m, order.Lines[0].OutstandingQuantity.Value);
        Assert.Equal(0m, order.Lines[1].ReceivedQuantity.Value);
        Assert.Equal(T0.AddHours(1), order.UpdatedAtUtc);
        Assert.Null(order.ClosedAtUtc);
    }

    [Fact]
    public void SeveralReceipts_AddUp_AndTheLastOneCompletesTheOrder()
    {
        var order = Placed();

        order.Receive([new ReceiptLine(RiceLine, Q(4)), new ReceiptLine(OilLine, Q(4))], T0.AddHours(1));
        order.Receive([new ReceiptLine(RiceLine, Q(6))], T0.AddHours(2));

        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
        Assert.Equal(10m, order.Lines[0].ReceivedQuantity.Value);
        Assert.True(order.Lines.All(line => line.IsFullyReceived));
        Assert.Equal(T0.AddHours(2), order.ClosedAtUtc);
    }

    [Fact]
    public void ReceivingEverythingAtOnce_GoesStraightToReceived()
    {
        var order = Placed();

        order.Receive([new ReceiptLine(RiceLine, Q(10)), new ReceiptLine(OilLine, Q(4))], T0.AddHours(1));

        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
    }

    [Fact]
    public void ReceivingFractionsOfAUnit_IsExact()
    {
        var order = Draft();
        order.AddLine(RiceLine, RiceId, "R", "Rice", "kg", Q(1), Gm(10), T0);
        order.Place(T0.AddMinutes(1));

        order.Receive([new ReceiptLine(RiceLine, Q(0.3333m))], T0.AddMinutes(2));
        order.Receive([new ReceiptLine(RiceLine, Q(0.6667m))], T0.AddMinutes(3));

        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
        Assert.Equal(1m, order.Lines[0].ReceivedQuantity.Value);
    }

    [Fact]
    public void ARejectedReceipt_ChangesNothing_NotEvenTheValidLinesOfIt()
    {
        var order = Placed();

        AssertRejected("purchase_order.receipt_exceeds_ordered", () => order.Receive([new ReceiptLine(RiceLine, Q(2)), new ReceiptLine(OilLine, Q(4.0001m))], T0.AddHours(1)));
        AssertRejected("purchase_order.receipt_quantity_required", () => order.Receive([new ReceiptLine(RiceLine, Q(2)), new ReceiptLine(OilLine, Q(0))], T0.AddHours(1)));
        AssertRejected("purchase_order.line_not_found", () => order.Receive([new ReceiptLine(RiceLine, Q(2)), new ReceiptLine(Guid.NewGuid(), Q(1))], T0.AddHours(1)));
        AssertRejected("purchase_order.receipt_line_duplicate", () => order.Receive([new ReceiptLine(RiceLine, Q(1)), new ReceiptLine(RiceLine, Q(1))], T0.AddHours(1)));
        AssertRejected("purchase_order.receipt_empty", () => order.Receive([], T0.AddHours(1)));

        Assert.Equal(PurchaseOrderStatus.Placed, order.Status);
        Assert.All(order.Lines, line => Assert.Equal(0m, line.ReceivedQuantity.Value));
        Assert.Equal(T0.AddMinutes(10), order.UpdatedAtUtc);
    }

    [Fact]
    public void ALineCannotReceiveMoreThanIsOutstanding_AcrossReceipts()
    {
        var order = Placed();
        order.Receive([new ReceiptLine(RiceLine, Q(9))], T0.AddHours(1));

        AssertRejected("purchase_order.receipt_exceeds_ordered", () => order.Receive([new ReceiptLine(RiceLine, Q(1.0001m))], T0.AddHours(2)));

        Assert.Equal(9m, order.Lines[0].ReceivedQuantity.Value);
    }

    [Fact]
    public void OnlyAPlacedOrPartiallyReceivedOrder_CanReceive()
    {
        AssertRejected("purchase_order.not_receivable", () => DraftWithTwoLines().Receive([new ReceiptLine(RiceLine, Q(1))], T0));

        var received = Placed();
        received.Receive([new ReceiptLine(RiceLine, Q(10)), new ReceiptLine(OilLine, Q(4))], T0.AddHours(1));
        AssertRejected("purchase_order.not_receivable", () => received.Receive([new ReceiptLine(RiceLine, Q(1))], T0.AddHours(2)));

        var cancelled = Placed();
        cancelled.Cancel(T0.AddHours(1));
        AssertRejected("purchase_order.not_receivable", () => cancelled.Receive([new ReceiptLine(RiceLine, Q(1))], T0.AddHours(2)));
    }

    // ---- cancelling

    [Fact]
    public void ADraftAndAPlacedOrder_CanBeCancelled()
    {
        var draft = DraftWithTwoLines();
        draft.Cancel(T0.AddMinutes(30));
        var placed = Placed();
        placed.Cancel(T0.AddMinutes(30));

        Assert.Equal(PurchaseOrderStatus.Cancelled, draft.Status);
        Assert.Equal(PurchaseOrderStatus.Cancelled, placed.Status);
        Assert.Equal(T0.AddMinutes(30), placed.ClosedAtUtc);
        Assert.Null(draft.PlacedAtUtc);
        Assert.Equal(T0.AddMinutes(10), placed.PlacedAtUtc);
    }

    [Fact]
    public void AnOrderThatHasReceivedAnything_OrIsClosed_CannotBeCancelled()
    {
        var partial = Placed();
        partial.Receive([new ReceiptLine(RiceLine, Q(1))], T0.AddHours(1));
        var received = Placed();
        received.Receive([new ReceiptLine(RiceLine, Q(10)), new ReceiptLine(OilLine, Q(4))], T0.AddHours(1));
        var cancelled = Placed();
        cancelled.Cancel(T0.AddHours(1));

        AssertRejected("purchase_order.cannot_cancel", () => partial.Cancel(T0.AddHours(2)));
        AssertRejected("purchase_order.cannot_cancel", () => received.Cancel(T0.AddHours(2)));
        AssertRejected("purchase_order.cannot_cancel", () => cancelled.Cancel(T0.AddHours(2)));
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, partial.Status);
    }

    [Fact]
    public void ACancelledOrder_CannotBeChangedFurther()
    {
        var order = DraftWithTwoLines();
        order.Cancel(T0.AddMinutes(30));

        AssertRejected("purchase_order.not_draft", () => order.AddLine(Guid.NewGuid(), Guid.NewGuid(), "X", "X", "x", Q(1), Gm(1), T0));
        AssertRejected("purchase_order.cannot_place", () => order.Place(T0));
        AssertRejected("purchase_order.not_receivable", () => order.Receive([new ReceiptLine(RiceLine, Q(1))], T0));
    }

    [Fact]
    public void EveryTimestampMustBeUtc()
    {
        var local = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.FromHours(2));
        var order = DraftWithTwoLines();

        AssertRejected("purchase_order.timestamp_not_utc", () => order.AddLine(Guid.NewGuid(), Guid.NewGuid(), "X", "X", "x", Q(1), Gm(1), local));
        AssertRejected("purchase_order.timestamp_not_utc", () => order.Place(local));
        order.Place(T0.AddMinutes(10));
        AssertRejected("purchase_order.timestamp_not_utc", () => order.Receive([new ReceiptLine(RiceLine, Q(1))], local));
        AssertRejected("purchase_order.timestamp_not_utc", () => order.Cancel(local));
        Assert.Equal(PurchaseOrderStatus.Placed, order.Status);
    }

    [Fact]
    public void TheLinesAreNotExposedForChange() => Assert.False(typeof(PurchaseOrder).GetProperty(nameof(PurchaseOrder.Lines))!.PropertyType.IsAssignableTo(typeof(ICollection<PurchaseOrderLine>)));
}
