using Soforr.Application.Common;
using Soforr.Application.Identity;

namespace Soforr.Application.Intelligence.GetInsights;

/// <summary>The current insights ("things that need your attention"), computed now from the organization's records.</summary>
public sealed record GetInsightsQuery;

/// <summary>An insight as it is returned to callers. Severity is its name (Info, Low, Medium, High).</summary>
public sealed record InsightDto(
    string Key,
    string Type,
    string Severity,
    InsightSubject Subject,
    IReadOnlyList<InsightFact> Facts,
    string Reason,
    InsightAction RecommendedAction)
{
    internal static InsightDto From(Insight insight)
    {
        ArgumentNullException.ThrowIfNull(insight);

        return new InsightDto(insight.Key, insight.Type, insight.Severity.ToString(), insight.Subject, insight.Facts, insight.Reason, insight.RecommendedAction);
    }
}

/// <summary>The evaluation: when it was computed and the insights, most urgent first.</summary>
public sealed record InsightsDto(DateTimeOffset EvaluatedAtUtc, IReadOnlyList<InsightDto> Items);

/// <summary>
/// Evaluates every registered <see cref="IInsightSource"/> and returns the combined insights in a fixed order: severity
/// (high first), then type, then the subject's name, then the subject's id, then the key. The result depends only on the
/// organization's records and the clock, so the same data and the same time give the same list. Nothing is stored: an
/// insight is computed on request and is never a record of what happened. A source that reveals costs runs only for a
/// caller who may see them; if any source fails the whole evaluation fails rather than silently omitting an insight.
/// </summary>
public sealed class GetInsightsHandler(IEnumerable<IInsightSource> sources, IClock clock, ICurrentUser user)
    : IQueryHandler<GetInsightsQuery, InsightsDto>
{
    public async Task<Result<InsightsDto>> HandleAsync(GetInsightsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.InsightsView) is { } denied)
        {
            return Result.Failure<InsightsDto>(denied);
        }

        var now = clock.UtcNow;
        var context = new InsightContext(now, user.Has(Permission.ProductViewCost));

        var insights = new List<Insight>();
        foreach (var source in sources.Where(source => !source.RequiresProductViewCost || context.CanSeeCosts))
        {
            insights.AddRange(await source.EvaluateAsync(context, cancellationToken));
        }

        var ordered = insights
            .OrderByDescending(insight => insight.Severity)
            .ThenBy(insight => insight.Type, StringComparer.Ordinal)
            .ThenBy(insight => insight.Subject.Name, StringComparer.Ordinal)
            .ThenBy(insight => insight.Subject.Id)
            .ThenBy(insight => insight.Key, StringComparer.Ordinal)
            .Select(InsightDto.From)
            .ToList();

        return Result.Success(new InsightsDto(now, ordered));
    }
}
