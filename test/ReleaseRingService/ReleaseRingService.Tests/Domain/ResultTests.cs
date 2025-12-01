using FluentAssertions;
using ReleaseRingService.Core.Errors;
using Xunit;

namespace ReleaseRingService.Tests.Domain;

public class ResultTests
{
    [Fact]
    public void Success_IsSuccess_ReturnsValue()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failure_IsFailure_ReturnsError()
    {
        var error = new Error("TEST_ERR", "Something went wrong");
        var result = Result<int>.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Value.Should().Be(default);
        result.Error.Should().Be(error);
    }

    [Fact]
    public void UnitResult_Success_HasNoError()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void UnitResult_Failure_HasError()
    {
        var error = new Error("TEST_ERR", "Something went wrong");
        var result = Result.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }
}
