namespace Soforr.Domain.Common;

/// <summary>
/// An amount in a specific currency. The amount has at most <see cref="MaxScale"/> decimal places, matching the
/// persisted precision, so a value that exists in the domain can always be stored exactly. Negative amounts are
/// allowed (refunds, adjustments); sign rules belong to the concept that uses the money (for example a price).
/// Multiplication and rounding are deliberately not provided yet: the rounding policy is decided with the first
/// feature that needs it (sales totals).
/// </summary>
public sealed record Money
{
    public const int MaxScale = 4;

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    /// <exception cref="DomainException">The currency is missing or the amount has more than four decimal places.</exception>
    public static Money Create(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (decimal.Round(amount, MaxScale) != amount)
        {
            throw new DomainException("money.scale_exceeded", $"An amount can have at most {MaxScale} decimal places.");
        }

        return new Money(amount, currency);
    }

    public static Money Zero(Currency currency) => Create(0m, currency);

    /// <exception cref="DomainException">The currencies differ.</exception>
    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    /// <exception cref="DomainException">The currencies differ.</exception>
    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    private void EnsureSameCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Currency != Currency)
        {
            throw new DomainException("money.currency_mismatch", "Amounts in different currencies cannot be combined.");
        }
    }
}
