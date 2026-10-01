using Soforr.Domain.Common;

namespace Soforr.Domain.Catalog;

/// <summary>
/// Something an organization buys and sells. A product belongs to exactly one organization.
/// A product is never deleted: once it has history it is deactivated, so past purchases, sales and stock
/// movements keep a valid reference to it.
/// SKU uniqueness within an organization is a rule across products, so it is not enforced by this entity: it is
/// case-insensitive and must be enforced by the database and reported by the handlers (ADR 0006). Likewise, that the
/// prices are in the organization's currency is checked by the handler, which knows the organization.
/// </summary>
public sealed class Product
{
    public const int MaxSkuLength = 64;
    public const int MaxNameLength = 200;
    public const int MaxUnitOfMeasureLength = 32;

    // Required by the persistence layer for materialization; not part of the domain API.
    private Product()
    {
        Sku = string.Empty;
        Name = string.Empty;
        UnitOfMeasure = string.Empty;
        SellingPrice = null!;
        CostPrice = null!;
    }

    private Product(
        Guid id,
        Guid organizationId,
        string sku,
        string name,
        string unitOfMeasure,
        Money sellingPrice,
        Money costPrice,
        ReorderSettings? reorderSettings,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        Sku = sku;
        Name = name;
        UnitOfMeasure = unitOfMeasure;
        SellingPrice = sellingPrice;
        CostPrice = costPrice;
        ReorderSettings = reorderSettings;
        IsActive = true;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// The SKU as entered, trimmed, with its casing preserved for display. It is deliberately not normalized:
    /// uniqueness is compared case-insensitively by persistence, not by changing the stored value (ADR 0006).
    /// </summary>
    public string Sku { get; private set; }

    public string Name { get; private set; }

    /// <summary>How the product is counted and sold, for example <c>each</c>, <c>kg</c> or <c>box</c>.</summary>
    public string UnitOfMeasure { get; private set; }

    public Money SellingPrice { get; private set; }

    /// <summary>
    /// Required. Zero is a valid stored cost; the model has no "unknown cost" state because no requirement
    /// establishes one.
    /// </summary>
    public Money CostPrice { get; private set; }

    /// <summary>Null when no reorder settings have been configured for the product.</summary>
    public ReorderSettings? ReorderSettings { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static Product Create(
        Guid id,
        Guid organizationId,
        string? sku,
        string? name,
        string? unitOfMeasure,
        Money sellingPrice,
        Money costPrice,
        ReorderSettings? reorderSettings,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("product.id_required", "A product must have an identifier.");
        }

        if (organizationId == Guid.Empty)
        {
            throw new DomainException("product.organization_required", "A product must belong to an organization.");
        }

        return new Product(
            id,
            organizationId,
            ValidSku(sku),
            ValidName(name),
            ValidUnitOfMeasure(unitOfMeasure),
            ValidPrice(sellingPrice, "selling"),
            ValidCostPrice(sellingPrice, costPrice),
            reorderSettings,
            RequireUtc(createdAtUtc));
    }

    /// <summary>
    /// Replaces the product's descriptive and commercial details. Every new value is validated before any state
    /// changes, so a rejected update leaves the product untouched.
    /// </summary>
    /// <exception cref="DomainException">An invariant is violated.</exception>
    public void Update(
        string? sku,
        string? name,
        string? unitOfMeasure,
        Money sellingPrice,
        Money costPrice,
        ReorderSettings? reorderSettings,
        DateTimeOffset updatedAtUtc)
    {
        var validSku = ValidSku(sku);
        var validName = ValidName(name);
        var validUnitOfMeasure = ValidUnitOfMeasure(unitOfMeasure);
        var validSellingPrice = ValidPrice(sellingPrice, "selling");
        var validCostPrice = ValidCostPrice(sellingPrice, costPrice);
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        Sku = validSku;
        Name = validName;
        UnitOfMeasure = validUnitOfMeasure;
        SellingPrice = validSellingPrice;
        CostPrice = validCostPrice;
        ReorderSettings = reorderSettings;
        UpdatedAtUtc = validUpdatedAtUtc;
    }

    /// <summary>
    /// Takes the product out of use without deleting it. Deactivating an inactive product changes nothing.
    /// </summary>
    /// <exception cref="DomainException">The timestamp is not UTC.</exception>
    public void Deactivate(DateTimeOffset updatedAtUtc)
    {
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        UpdatedAtUtc = validUpdatedAtUtc;
    }

    private static string ValidSku(string? sku)
    {
        var trimmed = sku?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("product.sku_required", "The SKU is required.");
        }

        if (trimmed.Length > MaxSkuLength)
        {
            throw new DomainException("product.sku_too_long", $"The SKU can have at most {MaxSkuLength} characters.");
        }

        return trimmed;
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("product.name_required", "The product name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException("product.name_too_long", $"The product name can have at most {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string ValidUnitOfMeasure(string? unitOfMeasure)
    {
        var trimmed = unitOfMeasure?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("product.unit_of_measure_required", "The unit of measure is required.");
        }

        if (trimmed.Length > MaxUnitOfMeasureLength)
        {
            throw new DomainException("product.unit_of_measure_too_long", $"The unit of measure can have at most {MaxUnitOfMeasureLength} characters.");
        }

        return trimmed;
    }

    private static Money ValidPrice(Money price, string which)
    {
        ArgumentNullException.ThrowIfNull(price);

        if (price.Amount < 0m)
        {
            throw new DomainException($"product.{which}_price_negative", $"The {which} price cannot be negative.");
        }

        return price;
    }

    private static Money ValidCostPrice(Money sellingPrice, Money costPrice)
    {
        var valid = ValidPrice(costPrice, "cost");

        if (valid.Currency != sellingPrice.Currency)
        {
            throw new DomainException("product.price_currency_mismatch", "The selling price and the cost price must be in the same currency.");
        }

        return valid;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainException("product.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return value;
    }
}
