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
