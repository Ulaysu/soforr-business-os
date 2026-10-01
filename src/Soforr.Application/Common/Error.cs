namespace Soforr.Application.Common;

/// <summary>
/// An expected failure returned by a handler. <see cref="Code"/> is stable and machine-readable. <see cref="Field"/>
/// names the input field the failure is about (in the camelCase name clients use), when there is one; it lets the
/// Api report validation problems per field.
/// </summary>
public sealed record Error
{
    public Error(string code, ErrorKind kind, string message, string? field = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Kind = kind;
        Message = message;
        Field = field;
    }

    public string Code { get; }

    public ErrorKind Kind { get; }

    public string Message { get; }

    public string? Field { get; }
}
