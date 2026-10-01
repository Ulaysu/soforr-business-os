using Soforr.Domain.Catalog;
using Soforr.Domain.Common;

namespace Soforr.Domain.Tests.Catalog;

public class ProductTests
{
    private static readonly Guid ProductId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a11");
    private static readonly Guid OrganizationId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c1a10");
    private static readonly Currency Usd = Currency.Create("USD");
    private static readonly Currency Eur = Currency.Create("EUR");
    private static readonly DateTimeOffset CreatedAt = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LaterAt = new(2026, 3, 1, 12, 30, 0, TimeSpan.Zero);

    private static Money Price(decimal amount, Currency? currency = null) => Money.Create(amount, currency ?? Usd);

    private static Product NewProduct(ReorderSettings? reorder = null) =>
        Product.Create(ProductId, OrganizationId, "RICE-25KG", "Rice 25kg bag", "bag", Price(30m), Price(22.5m), reorder, CreatedAt);

    // ---- creation

    [Fact]
    public void Create_ProducesAnActiveProductWithTheGivenValues()
    {
        var reorder = ReorderSettings.Create(Quantity.Create(10m), 7);

        var product = NewProduct(reorder);

        Assert.Equal(ProductId, product.Id);
        Assert.Equal(OrganizationId, product.OrganizationId);
        Assert.Equal("RICE-25KG", product.Sku);
        Assert.Equal("Rice 25kg bag", product.Name);
        Assert.Equal("bag", product.UnitOfMeasure);
        Assert.Equal(Price(30m), product.SellingPrice);
        Assert.Equal(Price(22.5m), product.CostPrice);
        Assert.Equal(reorder, product.ReorderSettings);
        Assert.True(product.IsActive);
        Assert.Equal(CreatedAt, product.CreatedAtUtc);
        Assert.Equal(CreatedAt, product.UpdatedAtUtc);
    }

    [Fact]
    public void Create_AllowsAProductWithoutReorderSettings()
    {
        Assert.Null(NewProduct().ReorderSettings);
    }

    [Fact]
    public void Create_TrimsSkuNameAndUnitOfMeasure()
    {
        var product = Product.Create(ProductId, OrganizationId, "  SKU-1 ", "  Widget  ", " each ", Price(1m), Price(0m), null, CreatedAt);

        Assert.Equal("SKU-1", product.Sku);
        Assert.Equal("Widget", product.Name);
        Assert.Equal("each", product.UnitOfMeasure);
    }

    [Fact]
    public void Create_RequiresAnIdentifierAndAnOrganization()
    {
        Assert.Equal(
            "product.id_required",
            Assert.Throws<DomainException>(() => Product.Create(Guid.Empty, OrganizationId, "S", "N", "u", Price(1m), Price(1m), null, CreatedAt)).Code);
        Assert.Equal(
            "product.organization_required",
            Assert.Throws<DomainException>(() => Product.Create(ProductId, Guid.Empty, "S", "N", "u", Price(1m), Price(1m), null, CreatedAt)).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RequiresASku(string? sku)
    {
        var exception = Assert.Throws<DomainException>(
            () => Product.Create(ProductId, OrganizationId, sku, "Name", "each", Price(1m), Price(1m), null, CreatedAt));

        Assert.Equal("product.sku_required", exception.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RequiresAName(string? name)
    {
        var exception = Assert.Throws<DomainException>(
            () => Product.Create(ProductId, OrganizationId, "SKU", name, "each", Price(1m), Price(1m), null, CreatedAt));

        Assert.Equal("product.name_required", exception.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RequiresAUnitOfMeasure(string? unit)
    {
        var exception = Assert.Throws<DomainException>(
            () => Product.Create(ProductId, OrganizationId, "SKU", "Name", unit, Price(1m), Price(1m), null, CreatedAt));

        Assert.Equal("product.unit_of_measure_required", exception.Code);
    }

    [Fact]
    public void Create_EnforcesTheLengthLimits_AtTheBoundary()
    {
        Assert.Equal(
            new string('s', Product.MaxSkuLength),
            Product.Create(ProductId, OrganizationId, new string('s', Product.MaxSkuLength), "N", "u", Price(1m), Price(1m), null, CreatedAt).Sku);
        Assert.Equal(
            "product.sku_too_long",
            Assert.Throws<DomainException>(() => Product.Create(ProductId, OrganizationId, new string('s', Product.MaxSkuLength + 1), "N", "u", Price(1m), Price(1m), null, CreatedAt)).Code);

        Assert.Equal(
            new string('n', Product.MaxNameLength),
            Product.Create(ProductId, OrganizationId, "S", new string('n', Product.MaxNameLength), "u", Price(1m), Price(1m), null, CreatedAt).Name);
        Assert.Equal(
            "product.name_too_long",
            Assert.Throws<DomainException>(() => Product.Create(ProductId, OrganizationId, "S", new string('n', Product.MaxNameLength + 1), "u", Price(1m), Price(1m), null, CreatedAt)).Code);

        Assert.Equal(
            new string('u', Product.MaxUnitOfMeasureLength),
            Product.Create(ProductId, OrganizationId, "S", "N", new string('u', Product.MaxUnitOfMeasureLength), Price(1m), Price(1m), null, CreatedAt).UnitOfMeasure);
        Assert.Equal(
            "product.unit_of_measure_too_long",
            Assert.Throws<DomainException>(() => Product.Create(ProductId, OrganizationId, "S", "N", new string('u', Product.MaxUnitOfMeasureLength + 1), Price(1m), Price(1m), null, CreatedAt)).Code);
    }

    // ---- prices

    [Fact]
    public void Create_AcceptsZeroPrices()
    {
        var product = Product.Create(ProductId, OrganizationId, "FREE", "Free sample", "each", Price(0m), Price(0m), null, CreatedAt);

        Assert.Equal(0m, product.SellingPrice.Amount);
        Assert.Equal(0m, product.CostPrice.Amount);
    }

    [Fact]
    public void Create_RejectsANegativeSellingPrice()
    {
        var exception = Assert.Throws<DomainException>(
            () => Product.Create(ProductId, OrganizationId, "S", "N", "u", Price(-0.01m), Price(1m), null, CreatedAt));

        Assert.Equal("product.selling_price_negative", exception.Code);
    }

    [Fact]
    public void Create_RejectsANegativeCostPrice()
    {
        var exception = Assert.Throws<DomainException>(
            () => Product.Create(ProductId, OrganizationId, "S", "N", "u", Price(1m), Price(-0.01m), null, CreatedAt));

        Assert.Equal("product.cost_price_negative", exception.Code);
    }

    [Fact]
    public void Create_RejectsPricesInDifferentCurrencies()
    {
        var exception = Assert.Throws<DomainException>(
            () => Product.Create(ProductId, OrganizationId, "S", "N", "u", Price(1m, Usd), Price(1m, Eur), null, CreatedAt));

        Assert.Equal("product.price_currency_mismatch", exception.Code);
    }

    [Fact]
    public void Create_RequiresBothPrices()
    {
        Assert.Throws<ArgumentNullException>(() => Product.Create(ProductId, OrganizationId, "S", "N", "u", null!, Price(1m), null, CreatedAt));
        Assert.Throws<ArgumentNullException>(() => Product.Create(ProductId, OrganizationId, "S", "N", "u", Price(1m), null!, null, CreatedAt));
    }

    [Fact]
    public void Create_RejectsATimestampThatIsNotUtc()
    {
        var notUtc = new DateTimeOffset(2026, 2, 10, 8, 0, 0, TimeSpan.FromHours(1));

        var exception = Assert.Throws<DomainException>(
            () => Product.Create(ProductId, OrganizationId, "S", "N", "u", Price(1m), Price(1m), null, notUtc));

        Assert.Equal("product.timestamp_not_utc", exception.Code);
    }

    // ---- update

    [Fact]
    public void Update_ReplacesTheDetails_AndRecordsWhenItHappened()
    {
        var product = NewProduct();
        var reorder = ReorderSettings.Create(Quantity.Create(40m), 21);

        product.Update(" RICE-50KG ", " Rice 50kg bag ", "sack", Price(55m, Eur), Price(41.25m, Eur), reorder, LaterAt);

        Assert.Equal("RICE-50KG", product.Sku);
        Assert.Equal("Rice 50kg bag", product.Name);
        Assert.Equal("sack", product.UnitOfMeasure);
        Assert.Equal(Price(55m, Eur), product.SellingPrice);
        Assert.Equal(Price(41.25m, Eur), product.CostPrice);
        Assert.Equal(reorder, product.ReorderSettings);
        Assert.Equal(LaterAt, product.UpdatedAtUtc);
        Assert.Equal(CreatedAt, product.CreatedAtUtc);
        Assert.Equal(ProductId, product.Id);
        Assert.Equal(OrganizationId, product.OrganizationId);
        Assert.True(product.IsActive);
    }

    [Fact]
    public void Update_CanClearTheReorderSettings()
    {
        var product = NewProduct(ReorderSettings.Create(Quantity.Create(10m), 7));

        product.Update("RICE-25KG", "Rice 25kg bag", "bag", Price(30m), Price(22.5m), null, LaterAt);

        Assert.Null(product.ReorderSettings);
    }

    public static TheoryData<string, string?, string?, string?, decimal, decimal, bool, bool> InvalidUpdates => new()
    {
        { "product.sku_required", "", "New name", "box", 10m, 5m, false, false },
        { "product.name_required", "NEW-SKU", "  ", "box", 10m, 5m, false, false },
        { "product.unit_of_measure_required", "NEW-SKU", "New name", null, 10m, 5m, false, false },
        { "product.selling_price_negative", "NEW-SKU", "New name", "box", -1m, 5m, false, false },
        { "product.cost_price_negative", "NEW-SKU", "New name", "box", 10m, -1m, false, false },
        { "product.price_currency_mismatch", "NEW-SKU", "New name", "box", 10m, 5m, true, false },
        { "product.timestamp_not_utc", "NEW-SKU", "New name", "box", 10m, 5m, false, true },
    };

    [Theory]
    [MemberData(nameof(InvalidUpdates))]
    public void Update_WithAnInvalidValue_ChangesNothing(
        string expectedCode,
        string? sku,
        string? name,
        string? unit,
        decimal sellingAmount,
        decimal costAmount,
        bool costInOtherCurrency,
        bool timestampNotUtc)
    {
        var reorder = ReorderSettings.Create(Quantity.Create(10m), 7);
        var product = NewProduct(reorder);
        var costCurrency = costInOtherCurrency ? Eur : Usd;
        var updatedAt = timestampNotUtc ? new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2)) : LaterAt;

        var exception = Assert.Throws<DomainException>(
            () => product.Update(sku, name, unit, Price(sellingAmount), Price(costAmount, costCurrency), null, updatedAt));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal("RICE-25KG", product.Sku);
        Assert.Equal("Rice 25kg bag", product.Name);
        Assert.Equal("bag", product.UnitOfMeasure);
        Assert.Equal(Price(30m), product.SellingPrice);
        Assert.Equal(Price(22.5m), product.CostPrice);
        Assert.Equal(reorder, product.ReorderSettings);
        Assert.Equal(CreatedAt, product.UpdatedAtUtc);
        Assert.True(product.IsActive);
    }

    // ---- deactivation

    [Fact]
    public void Deactivate_TakesTheProductOutOfUse_WithoutLosingItsData()
    {
        var product = NewProduct(ReorderSettings.Create(Quantity.Create(10m), 7));

        product.Deactivate(LaterAt);

        Assert.False(product.IsActive);
        Assert.Equal(LaterAt, product.UpdatedAtUtc);
        Assert.Equal("RICE-25KG", product.Sku);
        Assert.Equal("Rice 25kg bag", product.Name);
        Assert.Equal(Price(30m), product.SellingPrice);
        Assert.NotNull(product.ReorderSettings);
    }

    [Fact]
    public void Deactivate_OnAnInactiveProduct_ChangesNothing()
    {
        var product = NewProduct();
        product.Deactivate(LaterAt);

        product.Deactivate(LaterAt.AddDays(5));

        Assert.False(product.IsActive);
        Assert.Equal(LaterAt, product.UpdatedAtUtc);
    }

    [Fact]
    public void Deactivate_RejectsATimestampThatIsNotUtc_AndChangesNothing()
    {
        var product = NewProduct();

        var exception = Assert.Throws<DomainException>(() => product.Deactivate(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.FromHours(1))));

        Assert.Equal("product.timestamp_not_utc", exception.Code);
        Assert.True(product.IsActive);
        Assert.Equal(CreatedAt, product.UpdatedAtUtc);
    }

    // ---- SKU casing (uniqueness itself is enforced by persistence, ADR 0006)

    [Theory]
    [InlineData("ABC-123")]
    [InlineData("abc-123")]
    [InlineData("Abc-123")]
    public void Create_PreservesTheCasingOfTheSku(string sku)
    {
        var product = Product.Create(ProductId, OrganizationId, sku, "Name", "each", Price(1m), Price(1m), null, CreatedAt);

        Assert.Equal(sku, product.Sku);
    }

    [Fact]
    public void Update_PreservesTheCasingOfTheNewSku_AndTrimsIt()
    {
        var product = NewProduct();

        product.Update("  Rice-25Kg  ", "Rice 25kg bag", "bag", Price(30m), Price(22.5m), null, LaterAt);

        Assert.Equal("Rice-25Kg", product.Sku);
    }

    // ---- update guards

    [Fact]
    public void Update_WithANullPrice_ThrowsAndChangesNothing()
    {
        var product = NewProduct();

        Assert.Throws<ArgumentNullException>(() => product.Update("NEW", "New", "box", null!, Price(1m), null, LaterAt));
        Assert.Throws<ArgumentNullException>(() => product.Update("NEW", "New", "box", Price(1m), null!, null, LaterAt));

        Assert.Equal("RICE-25KG", product.Sku);
        Assert.Equal(Price(30m), product.SellingPrice);
        Assert.Equal(Price(22.5m), product.CostPrice);
        Assert.Equal(CreatedAt, product.UpdatedAtUtc);
    }

    [Fact]
    public void Update_OnADeactivatedProduct_DoesNotReactivateIt()
    {
        var product = NewProduct();
        product.Deactivate(LaterAt);

        product.Update("RICE-25KG", "Rice 25kg bag (renamed)", "bag", Price(31m), Price(23m), null, LaterAt.AddDays(1));

        Assert.False(product.IsActive);
        Assert.Equal("Rice 25kg bag (renamed)", product.Name);
    }
}
