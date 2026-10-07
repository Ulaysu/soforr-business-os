using Soforr.Domain.Common;

namespace Soforr.Domain.Sales;

/// <summary>
/// One product on a sale. It records, <b>as they were when the sale happened</b>, the product's SKU, name and unit of
/// measure, the catalog unit price, the final unit price actually applied, the cost price and the currency (snapshots, not
/// references to the product's current values), because products stay editable and sales and margin intelligence must
/// not change when they are. A line is created only through its <see cref="Sale"/> and is never changed afterwards.
/// </summary>
public sealed class SaleLine
{
    public const int MaxSkuLength = 64;
    public const int MaxNameLength = 200;
    public const int MaxUnitOfMeasureLength = 32;
    public const int MaxOverrideReasonLength = 200;

    // Required by the persistence layer for materialization; not part of the domain API.
    private SaleLine()
    {
        SkuSnapshot = string.Empty;
        NameSnapshot = string.Empty;
        UnitOfMeasureSnapshot = string.Empty;
        Quantity = null!;
        CatalogUnitPrice = null!;
        UnitPrice = null!;
        CostPrice = null!;
    }

    internal SaleLine(
        Guid id,
        Guid organizationId,
        Guid saleId,
        Guid productId,
        string skuSnapshot,
        string nameSnapshot,
        string unitOfMeasureSnapshot,
        Quantity quantity,
        Money catalogUnitPrice,
        Money unitPrice,
        Money costPrice,
        string? priceOverrideReason)
    {
        Id = id;
        OrganizationId = organizationId;
        SaleId = saleId;
        ProductId = productId;
        SkuSnapshot = skuSnapshot;
        NameSnapshot = nameSnapshot;
        UnitOfMeasureSnapshot = unitOfMeasureSnapshot;
        Quantity = quantity;

        // Each price is its own value: the same instance is often passed for two of them (an unchanged price is the catalog
        // price), and persistence cannot track one value under two properties.
        CatalogUnitPrice = Money.Create(catalogUnitPrice.Amount, catalogUnitPrice.Currency);
        UnitPrice = Money.Create(unitPrice.Amount, unitPrice.Currency);
        CostPrice = Money.Create(costPrice.Amount, costPrice.Currency);
        PriceOverrideReason = priceOverrideReason;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid SaleId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>The product's SKU when the sale happened.</summary>
    public string SkuSnapshot { get; private set; }

    /// <summary>The product's name when the sale happened.</summary>
    public string NameSnapshot { get; private set; }

    /// <summary>The product's unit of measure when the sale happened.</summary>
    public string UnitOfMeasureSnapshot { get; private set; }

    /// <summary>How much was sold. Always greater than zero.</summary>
    public Quantity Quantity { get; private set; }

    /// <summary>The product's selling price when the sale happened: what the price would have been without an override or discount.</summary>
    public Money CatalogUnitPrice { get; private set; }

    /// <summary>The price actually charged for one unit. Never negative; equal to <see cref="CatalogUnitPrice"/> unless the line was overridden or discounted.</summary>
    public Money UnitPrice { get; private set; }

    /// <summary>The product's cost price when the sale happened, so margin never reads the product's current cost.</summary>
    public Money CostPrice { get; private set; }

    /// <summary>Why the price differs from the catalog price. Present exactly when <see cref="IsPriceOverridden"/>.</summary>
    public string? PriceOverrideReason { get; private set; }

    /// <summary>Whether the price charged differs from the catalog price (an override or a discount).</summary>
    public bool IsPriceOverridden => UnitPrice.Amount != CatalogUnitPrice.Amount;

    /// <summary>What was charged for this line: the final unit price times the quantity (rounding policy: <see cref="Money.Multiply"/>).</summary>
    public Money LineTotal => UnitPrice.Multiply(Quantity);

    /// <summary>What the goods on this line cost: the cost price times the quantity.</summary>
    public Money LineCost => CostPrice.Multiply(Quantity);

    /// <summary>The amount taken off the catalog price for this line (negative when the line was sold above the catalog price).</summary>
    public Money DiscountTotal => CatalogUnitPrice.Multiply(Quantity).Subtract(LineTotal);
}
