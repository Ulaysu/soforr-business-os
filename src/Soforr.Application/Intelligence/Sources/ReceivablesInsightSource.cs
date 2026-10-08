using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Persistence;

namespace Soforr.Application.Intelligence.Sources;

/// <summary>
/// The overdue customer balance insights for the attention list (<see cref="OverdueBalanceRule"/>): one per customer whose overdue
/// amount reaches the organization's materiality threshold (every overdue balance when none is set), with the aging, the days and the
/// sales behind it. Money owed to the business is not a cost price or a margin, so the source does not require <c>ProductViewCost</c>.
/// </summary>
public sealed class ReceivablesInsightSource(IAppDbContext db) : IInsightSource
{
    public bool RequiresProductViewCost => false;

    public async Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var input = await ReceivablesGatherer.GatherAsync(db, cancellationToken);
        var receivables = ReceivablesCalculator.Calculate(input.Sales, input.Customers, context.NowUtc, input.Zone);

        return OverdueBalanceRule.Evaluate(receivables, input.MaterialityThreshold);
    }
}
