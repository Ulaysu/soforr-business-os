using Soforr.Domain.Common;

namespace Soforr.Domain.Inventory;

/// <summary>
/// A signed, non-zero change to a stock quantity: positive adds stock, negative removes it. It has at most
/// <see cref="Quantity.MaxScale"/> decimal places and its magnitude fits <see cref="Quantity.MaxValue"/>, so a
/// stored movement can always be applied to a stored stock level exactly. This is the signed counterpart of the
/// non-negative <see cref="Quantity"/>.
/// </summary>
public sealed record QuantityDelta
{
    private QuantityDelta(decimal value)
    {
        Value = value;
    }

    public decimal Value { get; }

    /// <exception cref="DomainException">The change is zero, has too many decimal places, or is too large.</exception>
    public static QuantityDelta Create(decimal value)
    {
        if (value == 0m)
        {
            throw new DomainException("quantity_delta.zero", "A stock change cannot be zero.");
        }

        if (decimal.Round(value, Quantity.MaxScale) != value)
        {
            throw new DomainException("quantity_delta.scale_exceeded", $"A stock change can have at most {Quantity.MaxScale} decimal places.");
        }

        if (Math.Abs(value) > Quantity.MaxValue)
        {
            throw new DomainException("quantity_delta.too_large", "The stock change is too large.");
        }

        return new QuantityDelta(value);
    }
}
