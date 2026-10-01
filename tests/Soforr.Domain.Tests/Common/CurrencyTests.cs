using Soforr.Domain.Common;

namespace Soforr.Domain.Tests.Common;

public class CurrencyTests
{
    [Theory]
    [InlineData("USD", "USD")]
    [InlineData("usd", "USD")]
    [InlineData("  gmd ", "GMD")]
    [InlineData("EUR", "EUR")]
    [InlineData("JPY", "JPY")]
    [InlineData("KWD", "KWD")]
    [InlineData("XOF", "XOF")]
    public void Create_AcceptsIsoCodes_AndNormalizesToUpperCase(string input, string expected)
    {
        var currency = Currency.Create(input);

        Assert.Equal(expected, currency.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("123")]
    [InlineData("U$D")]
    [InlineData("ZZZ")]
    [InlineData("XXX")]
    [InlineData("XAU")]
    public void Create_RejectsAnythingThatIsNotAnIsoCurrencyCode(string? input)
    {
        var exception = Assert.Throws<DomainException>(() => Currency.Create(input));

        Assert.Equal("currency.invalid", exception.Code);
    }

    [Fact]
    public void Currencies_WithTheSameCode_AreEqual_RegardlessOfInputCasing()
    {
        Assert.Equal(Currency.Create("usd"), Currency.Create("USD"));
        Assert.NotEqual(Currency.Create("USD"), Currency.Create("EUR"));
    }
}
