using System.Diagnostics.CodeAnalysis;

namespace Soforr.Domain.Common;

/// <summary>
/// Raised when an operation would violate a domain invariant. <see cref="Code"/> is a stable, machine-readable
/// identifier (for example <c>organization.name_required</c>) that the Application layer maps to an error result.
/// Domain objects cannot be constructed in or moved to an invalid state, so this is the last line of defence.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "A domain exception always carries a stable error code.")]
public sealed class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        Code = code;
    }

    public string Code { get; }
}
