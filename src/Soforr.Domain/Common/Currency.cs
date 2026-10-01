namespace Soforr.Domain.Common;

/// <summary>A supported ISO 4217 alphabetic currency code (see <c>IsoCurrencyCodes</c>), normalized to upper case.</summary>
public sealed record Currency
{
    private Currency(string code)
    {
        Code = code;
    }

    public string Code { get; }

    /// <exception cref="DomainException">The value is not in Soforr's supported ISO 4217 currency set.</exception>
    public static Currency Create(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(normalized) || !IsoCurrencyCodes.Contains(normalized))
        {
            throw new DomainException("currency.invalid", "The currency must be a supported ISO 4217 currency code.");
        }

        return new Currency(normalized);
    }

    public override string ToString() => Code;
}
