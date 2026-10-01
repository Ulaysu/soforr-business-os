namespace Soforr.Application.Common;

/// <summary>Creates results. Handlers return a <see cref="Result{T}"/> instead of throwing for expected failures.</summary>
public static class Result
{
    public static Result<T> Success<T>(T value) => new(value, error: null);

    public static Result<T> Failure<T>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new Result<T>(default, error);
    }
}

/// <summary>Either a value or an <see cref="Common.Error"/>.</summary>
public sealed class Result<T>
{
    private readonly T? _value;

    internal Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public bool IsFailure => !IsSuccess;

    /// <summary>The error of a failed result; null when the result succeeded.</summary>
    public Error? Error { get; }

    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error!.Code}).");
}
