using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Persistence;

namespace Soforr.Application.Intelligence.Sources;

/// <summary>
/// The purchasing cost insights for the attention list (<see cref="PurchaseCostRule"/>): notable supplier cost changes and supplier cost
/// gaps from purchases placed in the last <see cref="PeriodDays"/> complete local days. Purchase costs are cost data, so the source requires
/// <c>ProductViewCost</c>.
/// </summary>
public sealed class PurchaseCostInsightSource(IAppDbContext db) : IInsightSource
{
    public const int PeriodDays = 30;

    public bool RequiresProductViewCost => true;

    public async Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var input = await PurchaseCostGatherer.GatherAsync(db, context.NowUtc, PeriodDays, cancellationToken);
        var costs = PurchaseCostCalculator.Calculate(input.Lines, input.Suppliers, input.Products, input.Recent);

        // Purchases placed today are recent too, so the window starts at the same instant but has no upper bound here.
        return PurchaseCostRule.Evaluate(costs, input.Recent.StartUtc);
    }
}
