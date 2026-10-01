using System.Globalization;
using Soforr.Domain.Common;

namespace Soforr.Domain.Tests.Common;

public class QuantityTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("12.5")]
    [InlineData("0.0001")]
    [InlineData("1.50000")]
    [InlineData("99999999999999.9999")]
    public void Create_AcceptsNonNegativeValuesThatFitTheStorageFormat(string value)
    {
        var parsed = decimal.Parse(value, CultureInfo.InvariantCulture);

        Assert.Equal(parsed, Quantity.Create(parsed).Value);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0.0001")]
    public void Create_RejectsNegativeValues(string value)
    {
        var exception = Assert.Throws<DomainException>(() => Quantity.Create(decimal.Parse(value, CultureInfo.InvariantCulture)));

        Assert.Equal("quantity.negative", exception.Code);
    }

    [Theory]
    [InlineData("1.23456")]
    [InlineData("0.00001")]
    public void Create_RejectsMoreThanFourDecimalPlaces(string value)
    {
        var exception = Assert.Throws<DomainException>(() => Quantity.Create(decimal.Parse(value, CultureInfo.InvariantCulture)));

        Assert.Equal("quantity.scale_exceeded", exception.Code);
    }

    [Theory]
    [InlineData("100000000000000")]
    [InlineData("100000000000000.0001")]
    public void Create_RejectsValuesLargerThanTheStorageFormatCanHold(string value)
    {
        var exception = Assert.Throws<DomainException>(() => Quantity.Create(decimal.Parse(value, CultureInfo.InvariantCulture)));

        Assert.Equal("quantity.too_large", exception.Code);
    }

    [Fact]
    public void Zero_IsAQuantityOfZero()
    {
        Assert.Equal(0m, Quantity.Zero.Value);
        Assert.Equal(Quantity.Create(0m), Quantity.Zero);
    }

    [Fact]
    public void Quantities_AreEqualByValue_IgnoringTrailingZeros()
    {
        Assert.Equal(Quantity.Create(2.5m), Quantity.Create(2.50m));
        Assert.NotEqual(Quantity.Create(2.5m), Quantity.Create(2.6m));
    }
}
