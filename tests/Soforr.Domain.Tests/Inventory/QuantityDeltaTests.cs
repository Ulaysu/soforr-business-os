using System.Globalization;
using Soforr.Domain.Common;
using Soforr.Domain.Inventory;

namespace Soforr.Domain.Tests.Inventory;

public class QuantityDeltaTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("12.5")]
    [InlineData("-12.5")]
    [InlineData("0.0001")]
    [InlineData("-0.0001")]
    [InlineData("99999999999999.9999")]
    [InlineData("-99999999999999.9999")]
    public void Create_AcceptsSignedNonZeroValuesThatFitTheStorageFormat(string value)
    {
        var parsed = decimal.Parse(value, CultureInfo.InvariantCulture);

        Assert.Equal(parsed, QuantityDelta.Create(parsed).Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0000")]
    public void Create_RejectsAZeroChange(string value)
    {
        var exception = Assert.Throws<DomainException>(() => QuantityDelta.Create(decimal.Parse(value, CultureInfo.InvariantCulture)));

        Assert.Equal("quantity_delta.zero", exception.Code);
    }

    [Theory]
    [InlineData("1.23456")]
    [InlineData("-0.00001")]
    public void Create_RejectsMoreThanFourDecimalPlaces(string value)
    {
        var exception = Assert.Throws<DomainException>(() => QuantityDelta.Create(decimal.Parse(value, CultureInfo.InvariantCulture)));

        Assert.Equal("quantity_delta.scale_exceeded", exception.Code);
    }

    [Theory]
    [InlineData("100000000000000")]
    [InlineData("-100000000000000")]
    public void Create_RejectsAMagnitudeLargerThanAQuantityCanHold(string value)
    {
        var exception = Assert.Throws<DomainException>(() => QuantityDelta.Create(decimal.Parse(value, CultureInfo.InvariantCulture)));

        Assert.Equal("quantity_delta.too_large", exception.Code);
    }

    [Fact]
    public void Deltas_AreEqualByValue_IgnoringTrailingZeros()
    {
        Assert.Equal(QuantityDelta.Create(-2.5m), QuantityDelta.Create(-2.50m));
        Assert.NotEqual(QuantityDelta.Create(2.5m), QuantityDelta.Create(-2.5m));
    }
}
