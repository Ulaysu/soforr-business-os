using Soforr.Application.Common;
using Soforr.Domain.Common;

namespace Soforr.Application.Tests.Common;

public class DomainErrorsTests
{
    [Fact]
    public void Run_ReturnsTheValue_WhenTheDomainAcceptsTheInput()
    {
        var result = DomainErrors.Run(() => Currency.Create("usd"));

        Assert.True(result.IsSuccess);
        Assert.Equal("USD", result.Value.Code);
    }

    [Fact]
    public void Run_TurnsADomainException_IntoAValidationError_WithItsCodeAndMessage()
    {
        var result = DomainErrors.Run(() => Currency.Create("not-a-currency"));

        Assert.True(result.IsFailure);
        Assert.Equal("currency.invalid", result.Error!.Code);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Message));
        Assert.Null(result.Error.Field);
    }

    [Fact]
    public void Run_AttachesTheFieldTheCodeIsAbout()
    {
        var result = DomainErrors.Run(() => Currency.Create("nope"), code => code == "currency.invalid" ? "currency" : null);

        Assert.Equal("currency", result.Error!.Field);
    }

    [Fact]
    public void Run_LeavesTheFieldEmpty_WhenTheMapperHasNoFieldForTheCode()
    {
        var result = DomainErrors.Run(() => Currency.Create("nope"), _ => null);

        Assert.Null(result.Error!.Field);
    }

    [Fact]
    public void Run_DoesNotSwallowAnythingButDomainExceptions()
    {
        Assert.Throws<InvalidOperationException>(() => DomainErrors.Run<int>(() => throw new InvalidOperationException("a bug, not a rejected input")));
    }

    [Fact]
    public void Run_RequiresAnOperation()
    {
        Assert.Throws<ArgumentNullException>(() => DomainErrors.Run<int>(null!));
    }

    [Fact]
    public void AnError_CarriesAnOptionalField()
    {
        Assert.Null(new Error("a.b", ErrorKind.Validation, "message").Field);
        Assert.Equal("name", new Error("a.b", ErrorKind.Validation, "message", "name").Field);
    }
}
