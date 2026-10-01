using Soforr.Application.Common;

namespace Soforr.Application.Tests.Common;

public class ResultTests
{
    [Fact]
    public void Success_CarriesTheValue_AndNoError()
    {
        var result = Result.Success("value");

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal("value", result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_CarriesTheError_AndRefusesToExposeAValue()
    {
        var error = new Error("organization.not_found", ErrorKind.NotFound, "The organization was not found.");

        var result = Result.Failure<string>(error);

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Failure_RequiresAnError()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure<string>(null!));
    }

    [Fact]
    public void ASuccessfulResult_MayCarryADefaultValue()
    {
        var result = Result.Success(0);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value);
    }

    [Theory]
    [InlineData("", "message")]
    [InlineData("  ", "message")]
    [InlineData("code", "")]
    public void Error_RequiresACodeAndAMessage(string code, string message)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Error(code, ErrorKind.Validation, message));
    }
}
