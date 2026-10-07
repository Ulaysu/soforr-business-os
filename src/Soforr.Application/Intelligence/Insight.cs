using System.Globalization;

namespace Soforr.Application.Intelligence;

/// <summary>
/// How much an insight needs the owner's attention. The order is fixed and documented (higher first on the attention
/// list): <c>High</c> is something that is costing or about to cost money now (for example a product that is out of
/// stock), <c>Medium</c> will need action soon, <c>Low</c> is worth knowing, <c>Info</c> is an observation with no action
/// required. Values start at 1 and are never renumbered.
/// </summary>
public enum InsightSeverity
{
    Info = 1,
    Low = 2,
    Medium = 3,
    High = 4,
}

/// <summary>What an insight is about: the kind of record, its id and its name as shown to people.</summary>
public sealed record InsightSubject(string Type, Guid? Id, string Name);

/// <summary>
/// One named number or value an insight was computed from, so the insight can be traced to records. The value is text in
/// the invariant culture (numbers without grouping, a dot as the decimal separator) and the unit, when there is one, is
/// named separately; presentation is the client's job.
/// </summary>
public sealed record InsightFact(string Key, string Value, string? Unit = null)
{
    public static InsightFact Number(string key, decimal value, string? unit = null) =>
        new(key, value.ToString("0.####", CultureInfo.InvariantCulture), unit);

    public static InsightFact Text(string key, string value) => new(key, value);
}

/// <summary>What the owner can do about an insight. The code is stable and machine-readable (the client maps it to a screen); the label is the plain sentence.</summary>
public sealed record InsightAction(string Code, string Label);

/// <summary>
/// A finding about the business, derived from authoritative records by an explicit rule (see the Intelligence conventions
/// in <c>docs/engineering/intelligence.md</c>). An insight is never the record of what happened and is never stored as a
/// source of truth. Every insight carries <b>the facts it was computed from</b> and <b>a plain reason</b>; an insight
/// without them cannot be created, so nothing can be shown that cannot be traced.
/// </summary>
public sealed record Insight
{
    private Insight(string type, InsightSeverity severity, InsightSubject subject, IReadOnlyList<InsightFact> facts, string reason, InsightAction recommendedAction)
    {
        Type = type;
        Severity = severity;
        Subject = subject;
        Facts = facts;
        Reason = reason;
        RecommendedAction = recommendedAction;
    }

    /// <summary>A stable, dotted code naming the kind of insight, for example <c>inventory.reorder_point_reached</c>.</summary>
    public string Type { get; }

    public InsightSeverity Severity { get; }

    public InsightSubject Subject { get; }

    /// <summary>The numbers and values the insight was built from. Never empty.</summary>
    public IReadOnlyList<InsightFact> Facts { get; }

    /// <summary>One plain sentence saying why this needs attention, using the facts. Never empty.</summary>
    public string Reason { get; }

    public InsightAction RecommendedAction { get; }

    /// <summary>
    /// Identifies this insight about this subject: the same data gives the same key, so a client can recognize it between
    /// evaluations. It is not a record id.
    /// </summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Type}:{Subject.Type}:{Subject.Id?.ToString("N") ?? "-"}");

    /// <exception cref="ArgumentException">The type, reason, facts or action are missing: this is a programming error in a rule, not bad user input.</exception>
    public static Insight Create(string type, InsightSeverity severity, InsightSubject subject, IReadOnlyList<InsightFact> facts, string reason, InsightAction recommendedAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(recommendedAction);

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown severity.");
        }

        if (facts.Count == 0)
        {
            throw new ArgumentException("An insight must carry the facts it was computed from.", nameof(facts));
        }

        if (facts.Any(fact => fact is null || string.IsNullOrWhiteSpace(fact.Key) || string.IsNullOrWhiteSpace(fact.Value)))
        {
            throw new ArgumentException("Every fact needs a key and a value.", nameof(facts));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subject.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendedAction.Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendedAction.Label);

        return new Insight(type, severity, subject, facts.ToArray(), reason, recommendedAction);
    }
}
