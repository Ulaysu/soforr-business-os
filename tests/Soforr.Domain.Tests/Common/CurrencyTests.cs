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

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("GBP")]
    [InlineData("JPY")]
    [InlineData("CHF")]
    [InlineData("GMD")]
    [InlineData("NGN")]
    [InlineData("GHS")]
    [InlineData("KES")]
    [InlineData("ZAR")]
    [InlineData("INR")]
    [InlineData("CNY")]
    [InlineData("BRL")]
    [InlineData("AED")]
    [InlineData("KWD")]
    [InlineData("XOF")]
    [InlineData("XAF")]
    [InlineData("XCD")]
    [InlineData("XPF")]
    [InlineData("XCG")]
    [InlineData("ZWG")]
    [InlineData("SLE")]
    [InlineData("VES")]
    [InlineData("MRU")]
    [InlineData("STN")]
    public void Create_AcceptsCurrentCurrencies_IncludingRecentRedenominations(string code)
    {
        Assert.Equal(code, Currency.Create(code).Code);
    }

    [Theory]
    [InlineData("HRK")]
    [InlineData("ZWL")]
    [InlineData("SLL")]
    [InlineData("VEF")]
    [InlineData("MRO")]
    [InlineData("STD")]
    [InlineData("BYR")]
    [InlineData("CUC")]
    [InlineData("LTL")]
    [InlineData("LVL")]
    [InlineData("DEM")]
    [InlineData("FRF")]
    [InlineData("ITL")]
    [InlineData("ZMK")]
    public void Create_RejectsWithdrawnAndHistoricalCurrencies(string code)
    {
        Assert.Equal("currency.invalid", Assert.Throws<DomainException>(() => Currency.Create(code)).Code);
    }

    [Theory]
    [InlineData("CHE")]
    [InlineData("CHW")]
    [InlineData("USN")]
    [InlineData("UYI")]
    [InlineData("UYW")]
    [InlineData("CLF")]
    [InlineData("COU")]
    [InlineData("MXV")]
    [InlineData("BOV")]
    public void Create_RejectsFundCodes(string code)
    {
        Assert.Equal("currency.invalid", Assert.Throws<DomainException>(() => Currency.Create(code)).Code);
    }

    [Theory]
    [InlineData("XAU")]
    [InlineData("XAG")]
    [InlineData("XPT")]
    [InlineData("XPD")]
    public void Create_RejectsPreciousMetalCodes(string code)
    {
        Assert.Equal("currency.invalid", Assert.Throws<DomainException>(() => Currency.Create(code)).Code);
    }

    [Theory]
    [InlineData("XTS")]
    [InlineData("XXX")]
    public void Create_RejectsTestAndNoCurrencyCodes(string code)
    {
        Assert.Equal("currency.invalid", Assert.Throws<DomainException>(() => Currency.Create(code)).Code);
    }

    [Theory]
    [InlineData(" usd ", "USD")]
    [InlineData("\tEur\n", "EUR")]
    [InlineData("gMd", "GMD")]
    public void Create_TrimsAndUpperCasesBeforeValidating(string input, string expected)
    {
        Assert.Equal(expected, Currency.Create(input).Code);
    }

    [Fact]
    public void ToString_IsTheCurrencyCode()
    {
        Assert.Equal("USD", Currency.Create("usd").ToString());
    }
}
