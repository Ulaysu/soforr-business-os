namespace Soforr.Application.Common;

/// <summary>An expected failure returned by a handler. <see cref="Code"/> is stable and machine-readable.</summary>
public sealed record Error
{
    public Error(string code, ErrorKind kind, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Kind = kind;
        Message = message;
    }

    public string Code { get; }

    public ErrorKind Kind { get; }

    public string Message { get; }
}
