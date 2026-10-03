using Soforr.Domain.Common;

namespace Soforr.Domain.Tests.Common;

public class MoneyTests
{
    private static readonly Currency Usd = Currency.Create("USD");
    private static readonly Currency Eur = Currency.Create("EUR");

    [Theory]
    [InlineData("0")]
    [InlineData("12.34")]
    [InlineData("1.2345")]
    [InlineData("1.50000")]
    [InlineData("-5.25")]
    public void Create_AcceptsAmountsWithAtMostFourSignificantDecimalPlaces(string amount)
    {
        var money = Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Usd);

        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), money.Amount);
        Assert.Equal(Usd, money.Currency);
    }

    [Theory]
    [InlineData("1.23456")]
    [InlineData("0.00001")]
    public void Create_RejectsAmountsThatCannotBeStoredExactly(string amount)
    {
        var exception = Assert.Throws<DomainException>(
            () => Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Usd));

        Assert.Equal("money.scale_exceeded", exception.Code);
    }

    [Fact]
    public void Add_And_Subtract_CombineAmountsInTheSameCurrency()
    {
        var ten = Money.Create(10.10m, Usd);
        var three = Money.Create(3.05m, Usd);

        Assert.Equal(Money.Create(13.15m, Usd), ten.Add(three));
        Assert.Equal(Money.Create(7.05m, Usd), ten.Subtract(three));
        Assert.Equal(Money.Create(-7.05m, Usd), three.Subtract(ten));
    }

    [Fact]
    public void Add_And_Subtract_RejectDifferentCurrencies()
    {
        var dollars = Money.Create(1m, Usd);
        var euros = Money.Create(1m, Eur);

        Assert.Equal("money.currency_mismatch", Assert.Throws<DomainException>(() => dollars.Add(euros)).Code);
        Assert.Equal("money.currency_mismatch", Assert.Throws<DomainException>(() => dollars.Subtract(euros)).Code);
    }

    // ---- range: the same contract as the numeric(19,4) column

    [Fact]
    public void MaxAmount_IsTheLargestValueOfNumeric19x4()
    {
        // 15 integer digits and 4 decimal places: 19 digits in all, the whole of numeric(19,4).
        Assert.Equal(999_999_999_999_999.9999m, Money.MaxAmount);
        Assert.Equal(19, Money.MaxAmount.ToString(System.Globalization.CultureInfo.InvariantCulture).Count(char.IsDigit));
    }

    [Fact]
    public void Create_AcceptsTheLargestAndTheSmallestAmount()
    {
        Assert.Equal(Money.MaxAmount, Money.Create(Money.MaxAmount, Usd).Amount);
        Assert.Equal(-Money.MaxAmount, Money.Create(-Money.MaxAmount, Usd).Amount);
    }

    [Theory]
    [InlineData("1000000000000000")]
    [InlineData("-1000000000000000")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    public void Create_RejectsAnAmountBeyondTheRangeOfTheStoredColumn(string amount)
    {
        var parsed = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        var exception = Assert.Throws<DomainException>(() => Money.Create(parsed, Usd));

        Assert.Equal("money.amount_out_of_range", exception.Code);
    }

    [Fact]
    public void Create_RejectsTheSmallestStepBeyondTheLargestAmount_AndBeyondTheSmallest()
    {
        const decimal Step = 0.0001m;

        Assert.Equal("money.amount_out_of_range", Assert.Throws<DomainException>(() => Money.Create(Money.MaxAmount + Step, Usd)).Code);
        Assert.Equal("money.amount_out_of_range", Assert.Throws<DomainException>(() => Money.Create(-Money.MaxAmount - Step, Usd)).Code);
    }

    [Fact]
    public void Add_AndSubtract_ThatLeaveTheRange_AreRejected_SoNoMoneyOutsideTheRangeExists()
    {
        var max = Money.Create(Money.MaxAmount, Usd);
        var one = Money.Create(1m, Usd);

        Assert.Equal("money.amount_out_of_range", Assert.Throws<DomainException>(() => max.Add(one)).Code);
        Assert.Equal("money.amount_out_of_range", Assert.Throws<DomainException>(() => Money.Create(-Money.MaxAmount, Usd).Subtract(one)).Code);
        Assert.Equal(Money.MaxAmount, max.Add(Money.Zero(Usd)).Amount);
        Assert.Equal(Money.MaxAmount - 1m, max.Subtract(one).Amount);
    }

    // ---- Multiply (a unit price times a quantity)

    [Theory]
    [InlineData("12.5", "4", "50")]
    [InlineData("90.5", "10", "905")]
    [InlineData("0", "1000", "0")]
    [InlineData("1.5", "0.3333", "0.5")]
    [InlineData("-2.5", "3", "-7.5")]
    [InlineData("0.0001", "0.5", "0.0001")]
    [InlineData("0.0001", "0.4999", "0")]
    [InlineData("-0.0001", "0.5", "-0.0001")]
    public void Multiply_IsExactToFourPlaces_RoundingHalfAwayFromZero(string amount, string quantity, string expected)
    {
        var product = Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Usd)
            .Multiply(Quantity.Create(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), product.Amount);
        Assert.Equal(Usd, product.Currency);
    }

    [Fact]
    public void Multiply_RejectsAResultOutsideTheRange_IncludingOneBeyondDecimal()
    {
        var max = Money.Create(Money.MaxAmount, Usd);

        Assert.Equal("money.amount_out_of_range", Assert.Throws<DomainException>(() => max.Multiply(Quantity.Create(2))).Code);
        Assert.Equal("money.amount_out_of_range", Assert.Throws<DomainException>(() => max.Multiply(Quantity.Create(Quantity.MaxValue))).Code);
        Assert.Equal(Money.MaxAmount, max.Multiply(Quantity.Create(1)).Amount);
        Assert.Equal(0m, max.Multiply(Quantity.Zero).Amount);
    }

    [Fact]
    public void Money_IsEqualByAmountAndCurrency_IgnoringTrailingZeros()
    {
        Assert.Equal(Money.Create(1.5m, Usd), Money.Create(1.50m, Usd));
        Assert.NotEqual(Money.Create(1.5m, Usd), Money.Create(1.5m, Eur));
        Assert.NotEqual(Money.Create(1.5m, Usd), Money.Create(1.6m, Usd));
    }

    [Fact]
    public void Zero_IsAnAmountOfZeroInTheGivenCurrency()
    {
        var zero = Money.Zero(Eur);

        Assert.Equal(0m, zero.Amount);
        Assert.Equal(Eur, zero.Currency);
    }

    [Fact]
    public void Create_RequiresACurrency()
    {
        Assert.Throws<ArgumentNullException>(() => Money.Create(1m, null!));
    }

    [Fact]
    public void FourDecimalPlaces_RoundTripExactlyThroughArithmetic()
    {
        var sum = Money.Zero(Usd);
        for (var i = 0; i < 10; i++)
        {
            sum = sum.Add(Money.Create(0.1m, Usd));
        }

        Assert.Equal(Money.Create(1m, Usd), sum);
    }

    [Fact]
    public void Zero_RequiresACurrency()
    {
        Assert.Throws<ArgumentNullException>(() => Money.Zero(null!));
    }

    [Fact]
    public void Add_And_Subtract_RejectANullOperand()
    {
        var money = Money.Create(1m, Usd);

        Assert.Throws<ArgumentNullException>(() => money.Add(null!));
        Assert.Throws<ArgumentNullException>(() => money.Subtract(null!));
    }

    [Fact]
    public void Arithmetic_DoesNotModifyTheOperands()
    {
        var left = Money.Create(5m, Usd);
        var right = Money.Create(2m, Usd);

        _ = left.Add(right);
        _ = left.Subtract(right);

        Assert.Equal(Money.Create(5m, Usd), left);
        Assert.Equal(Money.Create(2m, Usd), right);
    }
}
