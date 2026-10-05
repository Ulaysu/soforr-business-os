using Soforr.Domain.Common;

namespace Soforr.Domain.Sales;

/// <summary>
/// The facts of one line to record on a sale. Everything the line snapshots is passed in (the Application layer reads it
/// from the product at the time of the sale); the Domain never looks anything up. <paramref name="UnitPrice"/> is the
/// price charged; when it differs from <paramref name="CatalogUnitPrice"/> a <paramref name="PriceOverrideReason"/> is required.
/// </summary>
public sealed record SaleLineInput(
    Guid LineId,
    Guid ProductId,
    string? SkuSnapshot,
    string? NameSnapshot,
    string? UnitOfMeasureSnapshot,
    Quantity Quantity,
    Money CatalogUnitPrice,
    Money UnitPrice,
    Money CostPrice,
    string? PriceOverrideReason);

/// <summary>
/// A sale of products to a walk-in customer, in one currency. It is recorded already completed and is immutable: its
/// lines, prices and totals never change; the only change that can follow is <see cref="Void"/>. Totals are computed
/// here from the lines, never supplied by a caller, and every value is validated completely before the sale exists, so a
/// rejected sale leaves nothing. The sale does not record stock: the application writes the matching inventory movements
/// in the same transaction (SALES-002). The user who recorded the sale is the person accountable for any price override
/// on its lines.
/// </summary>
public sealed class Sale
{
    public const int MaxVoidReasonLength = 500;

    private readonly List<SaleLine> _lines = [];

    // Required by the persistence layer for materialization; not part of the domain API.
    private Sale()
    {
        Currency = null!;
    }

    private Sale(Guid id, Guid organizationId, Currency currency, Guid createdByUserId, DateTimeOffset createdAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        Currency = currency;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        Status = SaleStatus.Completed;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The currency of every amount on the sale (the organization's currency). The Domain only stores it: the Application layer obtains the authoritative currency and passes it in.</summary>
    public Currency Currency { get; private set; }

    public SaleStatus Status { get; private set; }

    public IReadOnlyList<SaleLine> Lines => _lines;

    /// <summary>The user who recorded the sale, and who is accountable for any price override on it.</summary>
    public Guid CreatedByUserId { get; private set; }

    /// <summary>When the sale happened.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? VoidedAtUtc { get; private set; }

    public Guid? VoidedByUserId { get; private set; }

    public string? VoidReason { get; private set; }

    /// <summary>What was charged: the sum of the line totals.</summary>
    public Money Total => _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum.Add(line.LineTotal));

    /// <summary>What the goods cost: the sum of the line costs (cost price at the time of the sale).</summary>
    public Money TotalCost => _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum.Add(line.LineCost));

    /// <summary>The total amount taken off catalog prices across the lines.</summary>
    public Money TotalDiscount => _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum.Add(line.DiscountTotal));

    /// <summary>Whether any line was sold at a price other than the catalog price.</summary>
    public bool HasPriceOverride => _lines.Any(line => line.IsPriceOverridden);

    /// <summary>
    /// Records a completed sale with its lines. Each product appears at most once. Quantities are greater than zero, prices
    /// and costs are not negative and are in the sale's currency, and a price that differs from the catalog price needs a
    /// reason (and a reason needs a differing price).
    /// </summary>
    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static Sale Complete(Guid id, Guid organizationId, Currency currency, Guid createdByUserId, DateTimeOffset createdAtUtc, IReadOnlyCollection<SaleLineInput> lines)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(lines);

        if (id == Guid.Empty)
        {
            throw new DomainException("sale.id_required", "A sale must have an identifier.");
        }

        if (organizationId == Guid.Empty)
        {
            throw new DomainException("sale.organization_required", "A sale must belong to an organization.");
        }

        if (createdByUserId == Guid.Empty)
        {
            throw new DomainException("sale.user_required", "A sale must be recorded by a user.");
        }

        var sale = new Sale(id, organizationId, currency, createdByUserId, RequireUtc(createdAtUtc));

        if (lines.Count == 0)
        {
            throw new DomainException("sale.no_lines", "A sale must have at least one line.");
        }

        foreach (var input in lines)
        {
            sale._lines.Add(BuildLine(sale, input));
        }

        if (sale._lines.Select(line => line.ProductId).Distinct().Count() != sale._lines.Count)
        {
            throw new DomainException("sale.line_product_duplicate", "A product can appear only once on a sale.");
        }

        if (sale._lines.Select(line => line.Id).Distinct().Count() != sale._lines.Count)
        {
            throw new DomainException("sale.line_id_duplicate", "Each line must have its own identifier.");
        }

        // The totals must be representable; computing them now refuses a sale whose total would not fit, before it exists.
        _ = sale.Total;
        _ = sale.TotalCost;
        _ = sale.TotalDiscount;

        return sale;
    }

    /// <summary>
    /// Cancels the sale after the fact: <c>Completed → Voided</c>. A reason and the user who voids it are required. Only a
    /// completed sale can be voided, and a void is final. The caller writes the stock-restoring movements in the same
    /// transaction.
    /// </summary>
    /// <exception cref="DomainException">The sale is already voided, the reason is missing or too long, or the user or time is invalid.</exception>
    public void Void(Guid voidedByUserId, string? reason, DateTimeOffset voidedAtUtc)
    {
        if (Status != SaleStatus.Completed)
        {
            throw new DomainException("sale.not_voidable", "Only a completed sale can be voided.");
        }

        if (voidedByUserId == Guid.Empty)
        {
            throw new DomainException("sale.void_user_required", "A void must name the user who voided the sale.");
        }

        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("sale.void_reason_required", "A reason is required to void a sale.");
        }

        if (trimmed.Length > MaxVoidReasonLength)
        {
            throw new DomainException("sale.void_reason_too_long", $"The reason can have at most {MaxVoidReasonLength} characters.");
        }

        var validVoidedAtUtc = RequireUtc(voidedAtUtc);
        if (validVoidedAtUtc < CreatedAtUtc)
        {
            throw new DomainException("sale.void_before_sale", "A sale cannot be voided before it happened.");
        }

        Status = SaleStatus.Voided;
        VoidedAtUtc = validVoidedAtUtc;
        VoidedByUserId = voidedByUserId;
        VoidReason = trimmed;
    }

    private static SaleLine BuildLine(Sale sale, SaleLineInput? input)
    {
        if (input is null)
        {
            throw new DomainException("sale.line_required", "A line is missing.");
        }

        ArgumentNullException.ThrowIfNull(input.Quantity);
        ArgumentNullException.ThrowIfNull(input.CatalogUnitPrice);
        ArgumentNullException.ThrowIfNull(input.UnitPrice);
        ArgumentNullException.ThrowIfNull(input.CostPrice);

        if (input.LineId == Guid.Empty)
        {
            throw new DomainException("sale.line_id_required", "A line must have an identifier.");
        }

        if (input.ProductId == Guid.Empty)
        {
            throw new DomainException("sale.line_product_required", "A line must be for a product.");
        }

        var sku = Snapshot(input.SkuSnapshot, SaleLine.MaxSkuLength, "sku");
        var name = Snapshot(input.NameSnapshot, SaleLine.MaxNameLength, "name");
        var unit = Snapshot(input.UnitOfMeasureSnapshot, SaleLine.MaxUnitOfMeasureLength, "unit_of_measure");

        if (input.Quantity.Value == 0m)
        {
            throw new DomainException("sale.line_quantity_required", "The quantity sold must be greater than zero.");
        }

        if (input.CatalogUnitPrice.Currency != sale.Currency || input.UnitPrice.Currency != sale.Currency || input.CostPrice.Currency != sale.Currency)
        {
            throw new DomainException("sale.line_currency_mismatch", "Every price and cost must be in the currency of the sale.");
        }

        if (input.UnitPrice.Amount < 0m)
        {
            throw new DomainException("sale.line_unit_price_negative", "The price charged cannot be negative.");
        }

        if (input.CatalogUnitPrice.Amount < 0m)
        {
            throw new DomainException("sale.line_catalog_price_negative", "The catalog price cannot be negative.");
        }

        if (input.CostPrice.Amount < 0m)
        {
            throw new DomainException("sale.line_cost_price_negative", "The cost price cannot be negative.");
        }

        var overridden = input.UnitPrice.Amount != input.CatalogUnitPrice.Amount;
        var reason = string.IsNullOrWhiteSpace(input.PriceOverrideReason) ? null : input.PriceOverrideReason.Trim();

        if (overridden && reason is null)
        {
            throw new DomainException("sale.line_override_reason_required", "A price that differs from the catalog price needs a reason.");
        }

        if (!overridden && reason is not null)
        {
            throw new DomainException("sale.line_override_reason_unexpected", "A reason can be given only for a price that differs from the catalog price.");
        }

        if (reason is { Length: > SaleLine.MaxOverrideReasonLength })
        {
            throw new DomainException("sale.line_override_reason_too_long", $"The price reason can have at most {SaleLine.MaxOverrideReasonLength} characters.");
        }

        return new SaleLine(input.LineId, sale.OrganizationId, sale.Id, input.ProductId, sku, name, unit, input.Quantity, input.CatalogUnitPrice, input.UnitPrice, input.CostPrice, reason);
    }

    private static string Snapshot(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException($"sale.line_{name}_required", $"The product's {name.Replace('_', ' ')} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"sale.line_{name}_too_long", $"The product's {name.Replace('_', ' ')} can have at most {maxLength} characters.");
        }

        return trimmed;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainException("sale.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return value;
    }
}
