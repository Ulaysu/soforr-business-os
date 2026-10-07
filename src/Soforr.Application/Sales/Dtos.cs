using Soforr.Domain.Sales;

namespace Soforr.Application.Sales;

/// <summary>
/// One line of a sale as it is returned to callers. The SKU, name, unit and prices are the ones recorded when the sale
/// happened. <see cref="CostPrice"/> and <see cref="LineCost"/> are null for a caller who may not see costs.
/// </summary>
public sealed record SaleLineDto(
    Guid Id,
    Guid ProductId,
    string Sku,
    string Name,
    string UnitOfMeasure,
    decimal Quantity,
    decimal CatalogUnitPrice,
    decimal UnitPrice,
    string? PriceOverrideReason,
    decimal LineTotal,
    decimal? CostPrice,
    decimal? LineCost)
{
    internal static SaleLineDto From(SaleLine line, bool includeCost)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new SaleLineDto(
            line.Id,
            line.ProductId,
            line.SkuSnapshot,
            line.NameSnapshot,
            line.UnitOfMeasureSnapshot,
            line.Quantity.Value,
            line.CatalogUnitPrice.Amount,
            line.UnitPrice.Amount,
            line.PriceOverrideReason,
            line.LineTotal.Amount,
            includeCost ? line.CostPrice.Amount : null,
            includeCost ? line.LineCost.Amount : null);
    }
}

/// <summary>One money event on a sale: a payment, or the reversal of one (<see cref="ReversesPaymentId"/> names the payment it takes back). Amounts are positive.</summary>
public sealed record SalePaymentDto(
    Guid Id,
    string Kind,
    decimal Amount,
    DateTimeOffset PaidAtUtc,
    string? MethodLabel,
    Guid? ReversesPaymentId,
    string? ReversalReason,
    Guid RecordedByUserId,
    DateTimeOffset RecordedAtUtc)
{
    internal static SalePaymentDto From(SalePayment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return new SalePaymentDto(payment.Id, payment.Kind.ToString(), payment.Amount.Amount, payment.PaidAtUtc, payment.MethodLabel, payment.ReversesPaymentId, payment.ReversalReason, payment.RecordedByUserId, payment.RecordedAtUtc);
    }
}

/// <summary>
/// A sale with its lines. <see cref="Total"/> is computed by the Domain, never supplied. <see cref="TotalCost"/> is null for a
/// caller who may not see costs. A sale made on account has a <see cref="CustomerId"/>, a fixed <see cref="DueAtUtc"/>, the
/// <see cref="AmountPaid"/> (payments minus reversals) and what is still <see cref="Outstanding"/>; a walk-in sale has no
/// customer or due date, is paid in full and has nothing outstanding.
/// </summary>
public sealed record SaleDto(
    Guid Id,
    string Status,
    string Currency,
    decimal Total,
    decimal TotalDiscount,
    decimal? TotalCost,
    IReadOnlyList<SaleLineDto> Lines,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? VoidedAtUtc,
    Guid? VoidedByUserId,
    string? VoidReason,
    Guid? CustomerId,
    DateTimeOffset? DueAtUtc,
    decimal AmountPaid,
    decimal Outstanding,
    IReadOnlyList<SalePaymentDto> Payments)
{
    internal static SaleDto From(Sale sale, bool includeCost)
    {
        ArgumentNullException.ThrowIfNull(sale);

        return new SaleDto(
            sale.Id,
            sale.Status.ToString(),
            sale.Currency.Code,
            sale.Total.Amount,
            sale.TotalDiscount.Amount,
            includeCost ? sale.TotalCost.Amount : null,
            // The database returns lines in no particular order; a stable order (name, SKU, then id) keeps responses repeatable.
            sale.Lines
                .OrderBy(line => line.NameSnapshot, StringComparer.Ordinal)
                .ThenBy(line => line.SkuSnapshot, StringComparer.Ordinal)
                .ThenBy(line => line.Id)
                .Select(line => SaleLineDto.From(line, includeCost))
                .ToList(),
            sale.CreatedByUserId,
            sale.CreatedAtUtc,
            sale.VoidedAtUtc,
            sale.VoidedByUserId,
            sale.VoidReason,
            sale.CustomerId,
            sale.DueAtUtc,
            sale.AmountPaid.Amount,
            sale.Outstanding.Amount,
            sale.Payments.OrderBy(payment => payment.RecordedAtUtc).ThenBy(payment => payment.Kind).ThenBy(payment => payment.Id).Select(SalePaymentDto.From).ToList());
    }
}

/// <summary>A sale without its lines, for lists.</summary>
public sealed record SaleSummaryDto(Guid Id, string Status, string Currency, decimal Total, int LineCount, Guid CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? VoidedAtUtc, Guid? CustomerId, DateTimeOffset? DueAtUtc, decimal Outstanding)
{
    internal static SaleSummaryDto From(Sale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        return new SaleSummaryDto(sale.Id, sale.Status.ToString(), sale.Currency.Code, sale.Total.Amount, sale.Lines.Count, sale.CreatedByUserId, sale.CreatedAtUtc, sale.VoidedAtUtc, sale.CustomerId, sale.DueAtUtc, sale.Outstanding.Amount);
    }
}

/// <summary>The outcome of recording a sale: the sale as it now stands. <see cref="AlreadyRecorded"/> is true when this sale had been recorded before and nothing changed.</summary>
public sealed record SaleResultDto(Guid SaleId, bool AlreadyRecorded, SaleDto Sale);
