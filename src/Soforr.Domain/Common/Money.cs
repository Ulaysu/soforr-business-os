namespace Soforr.Domain.Common;

/// <summary>
/// An amount in a specific currency. The amount has at most <see cref="MaxScale"/> decimal places and its magnitude
/// is at most <see cref="MaxAmount"/>, matching the persisted <c>numeric(19,4)</c>, so a value that exists in the
/// domain can always be stored exactly. Negative amounts are allowed (refunds, adjustments), within the same
/// magnitude; sign rules belong to the concept that uses the money (for example a price).
/// Multiplication and rounding are deliberately not provided yet: the rounding policy is decided with the first
/// feature that needs it (sales totals).
/// </summary>
public sealed record Money
{
    public const int MaxScale = 4;

    /// <summary>
    /// The largest magnitude that fits <c>numeric(19,4)</c>: 15 integer digits and 4 decimal places. The smallest
    /// amount is its negation. This is one contract with the database column, not an arbitrary business limit:
    /// widening the column means changing this constant in the same change.
    /// </summary>
    public const decimal MaxAmount = 999_999_999_999_999.9999m;

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    /// <exception cref="DomainException">The amount has more than four decimal places or is larger in magnitude than <see cref="MaxAmount"/>.</exception>
    public static Money Create(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (decimal.Round(amount, MaxScale) != amount)
        {
            throw new DomainException("money.scale_exceeded", $"An amount can have at most {MaxScale} decimal places.");
        }

        if (amount is > MaxAmount or < -MaxAmount)
        {
            throw new DomainException("money.amount_out_of_range", "The amount is too large.");
        }

        return new Money(amount, currency);
    }

    public static Money Zero(Currency currency) => Create(0m, currency);

    /// <exception cref="DomainException">The currencies differ, or the sum is outside the range of <see cref="MaxAmount"/>.</exception>
    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return Create(Amount + other.Amount, Currency);
    }

    /// <exception cref="DomainException">The currencies differ, or the difference is outside the range of <see cref="MaxAmount"/>.</exception>
    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return Create(Amount - other.Amount, Currency);
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
