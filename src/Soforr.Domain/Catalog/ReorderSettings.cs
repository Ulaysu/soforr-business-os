using Soforr.Domain.Common;

namespace Soforr.Domain.Catalog;

/// <summary>
/// When a product should be reordered: the stock level at which to reorder and how many days a supplier takes to
/// deliver. They are inputs to reorder recommendations and carry no behavior of their own.
/// </summary>
public sealed record ReorderSettings
{
    private ReorderSettings(Quantity reorderPoint, int leadTimeDays)
    {
        ReorderPoint = reorderPoint;
        LeadTimeDays = leadTimeDays;
    }

    public Quantity ReorderPoint { get; }

    public int LeadTimeDays { get; }

    /// <exception cref="DomainException">The lead time is negative.</exception>
    public static ReorderSettings Create(Quantity reorderPoint, int leadTimeDays)
    {
        ArgumentNullException.ThrowIfNull(reorderPoint);

        if (leadTimeDays < 0)
        {
            throw new DomainException("reorder_settings.lead_time_negative", "The lead time cannot be negative.");
        }

        return new ReorderSettings(reorderPoint, leadTimeDays);
    }
}
