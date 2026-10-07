namespace Soforr.Application.Intelligence;

/// <summary>
/// What a source needs to judge: the evaluation time (passed in, never read from the system clock inside a rule, so the
/// same data and the same time always give the same result) and whether the caller may see costs. The organization is not
/// here: every source reads through the tenant-filtered database context, which only ever sees the caller's organization.
/// </summary>
public sealed record InsightContext(DateTimeOffset NowUtc, bool CanSeeCosts);

/// <summary>
/// A source of insights for one capability (inventory risk, sales comparison, margins, ...). It <b>gathers</b> exactly the
/// authoritative records its rules need (read-only, tenant-filtered, never saving) and hands them to <b>pure rules</b>
/// that judge them. Adding a capability means adding a source: the evaluation finds every registered source, so nothing is
/// listed by hand and only capabilities that exist appear.
/// </summary>
public interface IInsightSource
{
    /// <summary>
    /// True when the insights reveal cost prices or margins. The evaluation then runs the source only for a caller who holds
    /// <c>ProductViewCost</c>; a source never has to check that itself.
    /// </summary>
    bool RequiresProductViewCost { get; }

    Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken);
}
