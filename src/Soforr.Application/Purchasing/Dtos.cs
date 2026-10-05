using Soforr.Domain.Purchasing;

namespace Soforr.Application.Purchasing;

/// <summary>A supplier as it is returned to callers; the Domain entity is never exposed.</summary>
public sealed record SupplierDto(Guid Id, string Name, string? ContactDetails, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
{
    internal static SupplierDto From(Supplier supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        return new SupplierDto(supplier.Id, supplier.Name, supplier.ContactDetails, supplier.IsActive, supplier.CreatedAtUtc, supplier.UpdatedAtUtc);
    }
}

/// <summary>One line of an order. The product's SKU, name and unit are the ones recorded when the line was added. Amounts are in the order's currency.</summary>
public sealed record PurchaseOrderLineDto(
    Guid Id,
    Guid ProductId,
    string Sku,
    string Name,
    string UnitOfMeasure,
    decimal OrderedQuantity,
    decimal ReceivedQuantity,
    decimal OutstandingQuantity,
    decimal UnitCost,
    decimal LineTotal)
{
    internal static PurchaseOrderLineDto From(PurchaseOrderLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new PurchaseOrderLineDto(
            line.Id,
            line.ProductId,
            line.SkuSnapshot,
            line.NameSnapshot,
            line.UnitOfMeasureSnapshot,
            line.OrderedQuantity.Value,
            line.ReceivedQuantity.Value,
            line.OutstandingQuantity.Value,
            line.UnitCost.Amount,
            line.LineTotal.Amount);
    }
}

/// <summary>A purchase order with its lines. <see cref="Total"/> is computed by the Domain, never supplied.</summary>
public sealed record PurchaseOrderDto(
    Guid Id,
    Guid SupplierId,
    string? Reference,
    string Status,
    string Currency,
    decimal Total,
    IReadOnlyList<PurchaseOrderLineDto> Lines,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? PlacedAtUtc,
    DateTimeOffset? ClosedAtUtc)
{
    internal static PurchaseOrderDto From(PurchaseOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new PurchaseOrderDto(
            order.Id,
            order.SupplierId,
            order.Reference,
            order.Status.ToString(),
            order.Currency.Code,
            order.Total.Amount,
            // The database returns lines in no particular order; a stable order (name, SKU, then id) keeps responses repeatable.
            order.Lines
                .OrderBy(line => line.NameSnapshot, StringComparer.Ordinal)
                .ThenBy(line => line.SkuSnapshot, StringComparer.Ordinal)
                .ThenBy(line => line.Id)
                .Select(PurchaseOrderLineDto.From)
                .ToList(),
            order.CreatedAtUtc,
            order.UpdatedAtUtc,
            order.PlacedAtUtc,
            order.ClosedAtUtc);
    }
}

/// <summary>An order without its lines, for lists.</summary>
public sealed record PurchaseOrderSummaryDto(
    Guid Id,
    Guid SupplierId,
    string? Reference,
    string Status,
    string Currency,
    decimal Total,
    int LineCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PlacedAtUtc,
    DateTimeOffset? ClosedAtUtc)
{
    internal static PurchaseOrderSummaryDto From(PurchaseOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new PurchaseOrderSummaryDto(order.Id, order.SupplierId, order.Reference, order.Status.ToString(), order.Currency.Code, order.Total.Amount, order.Lines.Count, order.CreatedAtUtc, order.PlacedAtUtc, order.ClosedAtUtc);
    }
}
