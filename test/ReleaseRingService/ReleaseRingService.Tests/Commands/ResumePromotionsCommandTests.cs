using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Metrics;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.Commands;
using ReleaseRingService.Infrastructure.DynamoDb;
using Xunit;

namespace ReleaseRingService.Tests.Commands;

public class ResumePromotionsCommandTests
{
    private readonly Mock<IDynamoDbRepository> _repo = new();
    private readonly Mock<IMetricsPublisher> _metrics = new();

    private ResumePromotionsCommand CreateSut() =>
        new(_repo.Object, _metrics.Object,
            Mock.Of<ILogger<ResumePromotionsCommand>>());

    private static SystemConfig CreateTestConfig(bool paused = false) =>
        new()
        {
            Region = "us-east-1",
            ParameterPrefix = "/release-rings/amis",
            Rings = RingName.All,
            SoakDurations = ImmutableDictionary<string, TimeSpan>.Empty,
            IsPromotionPaused = paused,
            PauseReason = paused ? "maintenance" : null
        };

    [Fact]
    public async Task Execute_WhenPaused_ClearsPauseAndRecordsTransition()
    {
        var config = CreateTestConfig(paused: true);
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        _repo.Verify(r => r.SaveSystemConfigAsync(
                It.Is<SystemConfig>(c =>
                    !c.IsPromotionPaused &&
                    c.PauseReason == null &&
                    c.Version == config.Version + 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.PromotionResumed),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_WhenAlreadyActive_IsIdempotent()
    {
        var config = CreateTestConfig(paused: false);
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        _repo.Verify(r => r.SaveSystemConfigAsync(
                It.IsAny<SystemConfig>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repo.Verify(r => r.AppendTransitionAsync(
                It.IsAny<TransitionRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_WhenNoConfiguration_ReturnsError()
    {
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((SystemConfig?)null);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NO_CONFIGURATION");

        _repo.Verify(r => r.SaveSystemConfigAsync(
                It.IsAny<SystemConfig>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_PreservesCurrentRingState()
    {
        var config = CreateTestConfig(paused: true);
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var sut = CreateSut();
        await sut.ExecuteAsync(CancellationToken.None);

        _repo.Verify(r => r.SaveRingStateAsync(
                It.IsAny<RingState>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
