using Soforr.Domain.Common;
using Soforr.Domain.Sales;

namespace Soforr.Domain.Tests.Sales;

public class SaleTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Seller = Guid.CreateVersion7();

    private static Money M(decimal amount) => Money.Create(amount, Gmd);

    private static SaleLineInput Line(decimal quantity = 2m, decimal catalog = 10m, decimal? price = null, decimal cost = 6m, string? reason = null, Guid? product = null, string? sku = "SKU-1", string? name = "Rice", string? unit = "bag") =>
        new(Guid.CreateVersion7(), product ?? Guid.CreateVersion7(), sku, name, unit, Quantity.Create(quantity), M(catalog), M(price ?? catalog), M(cost), reason);

    private static Sale Complete(params SaleLineInput[] lines) =>
        Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, lines);

    private static string CodeOf(Action action) => Assert.Throws<DomainException>(action).Code;

    // ---- a completed sale

    [Fact]
    public void ASaleIsRecordedCompleted_WithItsOwnerCurrencyActorAndTime()
    {
        var sale = Complete(Line());

        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.Equal(Organization, sale.OrganizationId);
        Assert.Equal(Gmd, sale.Currency);
        Assert.Equal(Seller, sale.CreatedByUserId);
        Assert.Equal(Now, sale.CreatedAtUtc);
        Assert.Null(sale.VoidedAtUtc);
        Assert.Null(sale.VoidedByUserId);
        Assert.Null(sale.VoidReason);
        Assert.All(sale.Lines, line => Assert.Equal((sale.Id, Organization), (line.SaleId, line.OrganizationId)));
    }

    [Fact]
    public void Totals_AreComputedFromTheLines()
    {
        var sale = Complete(Line(quantity: 3m, catalog: 10m, cost: 6m, sku: "A"), Line(quantity: 0.5m, catalog: 25m, cost: 15m, sku: "B"));

        Assert.Equal(30m, sale.Lines[0].LineTotal.Amount);
        Assert.Equal(12.5m, sale.Lines[1].LineTotal.Amount);
        Assert.Equal(42.5m, sale.Total.Amount);
        Assert.Equal(25.5m, sale.TotalCost.Amount);
        Assert.Equal(0m, sale.TotalDiscount.Amount);
        Assert.False(sale.HasPriceOverride);
        Assert.Equal(Gmd, sale.Total.Currency);
    }

    [Fact]
    public void ALineTotal_IsRoundedToTheStoredPrecision_HalfAwayFromZero()
    {
        // 0.3333 x 0.0005 = 0.00016665 -> 0.0002
        var sale = Complete(Line(quantity: 0.3333m, catalog: 0.0005m, cost: 0m));

        Assert.Equal(0.0002m, sale.Lines[0].LineTotal.Amount);
    }

    [Fact]
    public void ALineKeepsTheSnapshotsItWasGiven_Trimmed()
    {
        var product = Guid.CreateVersion7();
        var sale = Complete(Line(quantity: 2m, catalog: 10m, price: 10m, cost: 6m, product: product, sku: "  RICE-25 ", name: " Rice 25kg ", unit: " bag "));

        var line = Assert.Single(sale.Lines);
        Assert.Equal(product, line.ProductId);
        Assert.Equal("RICE-25", line.SkuSnapshot);
        Assert.Equal("Rice 25kg", line.NameSnapshot);
        Assert.Equal("bag", line.UnitOfMeasureSnapshot);
        Assert.Equal(10m, line.CatalogUnitPrice.Amount);
        Assert.Equal(10m, line.UnitPrice.Amount);
        Assert.Equal(6m, line.CostPrice.Amount);
        Assert.Equal(Gmd, line.UnitPrice.Currency);
        Assert.Null(line.PriceOverrideReason);
    }

    // ---- price overrides and discounts

    [Fact]
    public void ADiscountedLine_KeepsTheCatalogPrice_TheFinalPrice_TheReasonAndTheDiscount()
    {
        var sale = Complete(Line(quantity: 4m, catalog: 10m, price: 8m, reason: "  Loyal customer  "));

        var line = Assert.Single(sale.Lines);
        Assert.True(line.IsPriceOverridden);
        Assert.Equal(10m, line.CatalogUnitPrice.Amount);
        Assert.Equal(8m, line.UnitPrice.Amount);
        Assert.Equal("Loyal customer", line.PriceOverrideReason);
        Assert.Equal(32m, line.LineTotal.Amount);
        Assert.Equal(8m, line.DiscountTotal.Amount);
        Assert.Equal(32m, sale.Total.Amount);
        Assert.Equal(8m, sale.TotalDiscount.Amount);
        Assert.True(sale.HasPriceOverride);
    }

    [Fact]
    public void ASaleAboveTheCatalogPrice_IsAnOverrideToo_WithANegativeDiscount()
    {
        var sale = Complete(Line(quantity: 2m, catalog: 10m, price: 12m, reason: "Rush order"));

        Assert.Equal(-4m, sale.TotalDiscount.Amount);
        Assert.Equal(24m, sale.Total.Amount);
    }

    [Fact]
    public void AFreeLine_IsAllowed_WithAReason()
    {
        var sale = Complete(Line(quantity: 1m, catalog: 10m, price: 0m, reason: "Free sample"));

        Assert.Equal(0m, sale.Total.Amount);
        Assert.Equal(10m, sale.TotalDiscount.Amount);
    }

    [Fact]
    public void ADifferentPriceWithoutAReason_IsRejected()
    {
        Assert.Equal("sale.line_override_reason_required", CodeOf(() => Complete(Line(catalog: 10m, price: 9m))));
        Assert.Equal("sale.line_override_reason_required", CodeOf(() => Complete(Line(catalog: 10m, price: 9m, reason: "   "))));
    }

    [Fact]
    public void AReasonWithoutADifferentPrice_IsRejected()
    {
        Assert.Equal("sale.line_override_reason_unexpected", CodeOf(() => Complete(Line(catalog: 10m, price: 10m, reason: "Because"))));
    }

    [Fact]
    public void AReasonThatIsTooLong_IsRejected()
    {
        Assert.Equal("sale.line_override_reason_too_long", CodeOf(() => Complete(Line(catalog: 10m, price: 9m, reason: new string('x', SaleLine.MaxOverrideReasonLength + 1)))));
        Assert.Single(Complete(Line(catalog: 10m, price: 9m, reason: new string('x', SaleLine.MaxOverrideReasonLength))).Lines);
    }

    [Fact]
    public void ANegativePrice_IsRejected()
    {
        Assert.Equal("sale.line_unit_price_negative", CodeOf(() => Complete(Line(catalog: 10m, price: -1m, reason: "Mistake"))));
    }

    // ---- validation

    [Fact]
    public void ASaleNeedsAtLeastOneLine()
    {
        Assert.Equal("sale.no_lines", CodeOf(() => Complete()));
    }

    [Fact]
    public void ANullLine_IsRejected()
    {
        Assert.Equal("sale.line_required", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, new SaleLineInput[] { null! })));
    }

    [Fact]
    public void AZeroQuantity_IsRejected()
    {
        Assert.Equal("sale.line_quantity_required", CodeOf(() => Complete(Line(quantity: 0m))));
    }

    [Fact]
    public void ANegativeCatalogPriceOrCost_IsRejected()
    {
        Assert.Equal("sale.line_catalog_price_negative", CodeOf(() => Complete(Line(catalog: -1m, price: 0m, reason: "Free"))));
        Assert.Equal("sale.line_cost_price_negative", CodeOf(() => Complete(Line(cost: -1m))));
    }

    [Fact]
    public void APriceOrCostInAnotherCurrency_IsRejected()
    {
        var usd = Currency.Create("USD");
        var other = new SaleLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), "S", "N", "u", Quantity.Create(1m), M(1m), M(1m), Money.Create(1m, usd), null);
        var otherPrice = new SaleLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), "S", "N", "u", Quantity.Create(1m), M(1m), Money.Create(1m, usd), M(1m), null);
        var otherCatalog = new SaleLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), "S", "N", "u", Quantity.Create(1m), Money.Create(1m, usd), M(1m), M(1m), null);

        Assert.Equal("sale.line_currency_mismatch", CodeOf(() => Complete(other)));
        Assert.Equal("sale.line_currency_mismatch", CodeOf(() => Complete(otherPrice)));
        Assert.Equal("sale.line_currency_mismatch", CodeOf(() => Complete(otherCatalog)));
    }

    [Theory]
    [InlineData(null, "Rice", "bag", "sale.line_sku_required")]
    [InlineData("  ", "Rice", "bag", "sale.line_sku_required")]
    [InlineData("S", null, "bag", "sale.line_name_required")]
    [InlineData("S", "Rice", " ", "sale.line_unit_of_measure_required")]
    public void MissingSnapshots_AreRejected(string? sku, string? name, string? unit, string code)
    {
        Assert.Equal(code, CodeOf(() => Complete(Line(sku: sku, name: name, unit: unit))));
    }

    [Fact]
    public void SnapshotsThatAreTooLong_AreRejected()
    {
        Assert.Equal("sale.line_sku_too_long", CodeOf(() => Complete(Line(sku: new string('x', SaleLine.MaxSkuLength + 1)))));
        Assert.Equal("sale.line_name_too_long", CodeOf(() => Complete(Line(name: new string('x', SaleLine.MaxNameLength + 1)))));
        Assert.Equal("sale.line_unit_of_measure_too_long", CodeOf(() => Complete(Line(unit: new string('x', SaleLine.MaxUnitOfMeasureLength + 1)))));
    }

    [Fact]
    public void TheSameProductTwice_IsRejected()
    {
        var product = Guid.CreateVersion7();

        Assert.Equal("sale.line_product_duplicate", CodeOf(() => Complete(Line(product: product), Line(product: product))));
    }

    [Fact]
    public void TheSameLineIdTwice_IsRejected()
    {
        var first = Line();
        var second = first with { ProductId = Guid.CreateVersion7() };

        Assert.Equal("sale.line_id_duplicate", CodeOf(() => Complete(first, second)));
    }

    [Fact]
    public void EmptyIdentifiersAndNonUtcTimes_AreRejected()
    {
        var lines = new[] { Line() };

        Assert.Equal("sale.id_required", CodeOf(() => Sale.Complete(Guid.Empty, Organization, Gmd, Seller, Now, lines)));
        Assert.Equal("sale.organization_required", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Guid.Empty, Gmd, Seller, Now, lines)));
        Assert.Equal("sale.user_required", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Guid.Empty, Now, lines)));
        Assert.Equal("sale.timestamp_not_utc", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(2)), lines)));
        Assert.Equal("sale.line_id_required", CodeOf(() => Complete(Line() with { LineId = Guid.Empty })));
        Assert.Equal("sale.line_product_required", CodeOf(() => Complete(Line() with { ProductId = Guid.Empty })));
    }

    [Fact]
    public void ATotalThatCannotBeRepresented_IsRejected_BeforeTheSaleExists()
    {
        var huge = Line(quantity: 1m, catalog: Money.MaxAmount, cost: 0m);
        var more = Line(quantity: 1m, catalog: 1m, cost: 0m);

        Assert.Equal("money.amount_out_of_range", CodeOf(() => Complete(huge, more)));
    }

    // ---- void

    [Fact]
    public void ACompletedSale_CanBeVoided_WithAReasonAndAnActor_AndKeepsItsLinesAndTotals()
    {
        var sale = Complete(Line(quantity: 2m, catalog: 10m));
        var voider = Guid.CreateVersion7();
        var total = sale.Total;

        sale.Void(voider, "  Wrong customer  ", Now.AddMinutes(5));

        Assert.Equal(SaleStatus.Voided, sale.Status);
        Assert.Equal(voider, sale.VoidedByUserId);
        Assert.Equal("Wrong customer", sale.VoidReason);
        Assert.Equal(Now.AddMinutes(5), sale.VoidedAtUtc);
        Assert.Equal(total, sale.Total);
        Assert.Single(sale.Lines);
    }

    [Fact]
    public void ASaleCanBeVoidedAtTheMomentItHappened()
    {
        var sale = Complete(Line());

        sale.Void(Guid.CreateVersion7(), "Oops", Now);

        Assert.Equal(SaleStatus.Voided, sale.Status);
    }

    [Fact]
    public void AVoidedSale_CannotBeVoidedAgain_AndIsUnchangedByTheAttempt()
    {
        var sale = Complete(Line());
        var voider = Guid.CreateVersion7();
        sale.Void(voider, "First", Now.AddMinutes(1));

        Assert.Equal("sale.not_voidable", CodeOf(() => sale.Void(Guid.CreateVersion7(), "Second", Now.AddMinutes(2))));

        Assert.Equal(voider, sale.VoidedByUserId);
        Assert.Equal("First", sale.VoidReason);
        Assert.Equal(Now.AddMinutes(1), sale.VoidedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AVoidWithoutAReason_IsRejected_AndChangesNothing(string? reason)
    {
        var sale = Complete(Line());

        Assert.Equal("sale.void_reason_required", CodeOf(() => sale.Void(Guid.CreateVersion7(), reason, Now.AddMinutes(1))));

        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.Null(sale.VoidedAtUtc);
    }

    [Fact]
    public void InvalidVoids_AreRejected_AndChangeNothing()
    {
        var sale = Complete(Line());

        Assert.Equal("sale.void_user_required", CodeOf(() => sale.Void(Guid.Empty, "Reason", Now.AddMinutes(1))));
        Assert.Equal("sale.void_reason_too_long", CodeOf(() => sale.Void(Guid.CreateVersion7(), new string('x', Sale.MaxVoidReasonLength + 1), Now.AddMinutes(1))));
        Assert.Equal("sale.timestamp_not_utc", CodeOf(() => sale.Void(Guid.CreateVersion7(), "Reason", new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.FromHours(1)))));
        Assert.Equal("sale.void_before_sale", CodeOf(() => sale.Void(Guid.CreateVersion7(), "Reason", Now.AddMinutes(-1))));

        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.Null(sale.VoidedByUserId);
        Assert.Null(sale.VoidReason);
    }
}
