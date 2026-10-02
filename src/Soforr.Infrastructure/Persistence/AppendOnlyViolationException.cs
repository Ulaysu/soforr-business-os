namespace Soforr.Infrastructure.Persistence;

/// <summary>
/// Code tried to change or delete a record that is append-only (an inventory movement). It is a defect, never an
/// expected failure, so it is an exception and surfaces as a 500.
/// </summary>
public sealed class AppendOnlyViolationException(string message) : InvalidOperationException(message);
