namespace Soforr.Infrastructure.Persistence;

/// <summary>
/// Code tried to change or delete a record that is append-only (an inventory movement, an audit event, a sale payment or a
/// sale line). It is a defect, never an expected failure, so it is an exception and surfaces as a 500.
/// </summary>
public sealed class AppendOnlyViolationException(string message) : InvalidOperationException(message);
