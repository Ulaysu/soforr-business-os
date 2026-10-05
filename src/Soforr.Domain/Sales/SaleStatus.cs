namespace Soforr.Domain.Sales;

/// <summary>
/// Where a sale is in its life. A sale is recorded already completed (it is never a draft), and the only change that can
/// follow is a void: <c>Completed → Voided</c>. <c>Voided</c> is final. The names are stored as text, so a value is never
/// renamed or renumbered.
/// </summary>
public enum SaleStatus
{
    /// <summary>The sale happened. Its lines, prices and totals can never change.</summary>
    Completed = 1,

    /// <summary>The sale was cancelled after the fact, with a reason, by a named user. The record stays; the stock it took is returned by new movements (SALES-002).</summary>
    Voided = 2,
}
