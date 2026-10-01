namespace Soforr.Domain.Common;

/// <summary>
/// A non-negative amount of something (units, kilograms, ...). It has at most <see cref="MaxScale"/> decimal places
/// and fits the persisted <c>numeric(18,4)</c> precision, so a value that exists in the domain can always be stored
/// exactly. The unit it is measured in belongs to the concept that uses it (for example a product's unit of measure).
/// Signed stock movements are a different concept and are not modeled here.
/// </summary>
public sealed record Quantity
{
    public const int MaxScale = 4;

    /// <summary>The largest value that fits <c>numeric(18,4)</c>: 14 integer digits and 4 decimal places.</summary>
    public const decimal MaxValue = 99_999_999_999_999.9999m;

    private Quantity(decimal value)
    {
        Value = value;
    }

    public static Quantity Zero { get; } = new(0m);

    public decimal Value { get; }

    /// <exception cref="DomainException">The value is negative, has too many decimal places, or is too large.</exception>
    public static Quantity Create(decimal value)
    {
        if (value < 0m)
        {
            throw new DomainException("quantity.negative", "A quantity cannot be negative.");
        }

        if (decimal.Round(value, MaxScale) != value)
        {
            throw new DomainException("quantity.scale_exceeded", $"A quantity can have at most {MaxScale} decimal places.");
        }

        if (value > MaxValue)
        {
            throw new DomainException("quantity.too_large", "The quantity is too large.");
        }

        return new Quantity(value);
    }
}
