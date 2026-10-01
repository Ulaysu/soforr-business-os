namespace Soforr.Application.Common;

/// <summary>The category of an expected failure. The Api maps each kind to an HTTP status code centrally.</summary>
public enum ErrorKind
{
    Validation,
    Unauthenticated,
    NotFound,
    Forbidden,
    Conflict,
    RuleViolation,
}
