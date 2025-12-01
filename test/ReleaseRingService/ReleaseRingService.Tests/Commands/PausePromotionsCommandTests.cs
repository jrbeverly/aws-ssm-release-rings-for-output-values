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

public class PausePromotionsCommandTests
{
    private readonly Mock<IDynamoDbRepository> _repo = new();
    private readonly Mock<IMetricsPublisher> _metrics = new();

    private PausePromotionsCommand CreateSut() =>
        new(_repo.Object, _metrics.Object,
            Mock.Of<ILogger<PausePromotionsCommand>>());

    private static SystemConfig CreateTestConfig(bool paused = false) =>
        new()
        {
            Region = "us-east-1",
            ParameterPrefix = "/release-rings/amis",
            Rings = RingName.All,
            SoakDurations = ImmutableDictionary<string, TimeSpan>.Empty,
            IsPromotionPaused = paused,
            PauseReason = paused ? "previous pause" : null
        };

    [Fact]
    public async Task Execute_WhenNotPaused_SetsPausedAndRecordsTransition()
    {
        var config = CreateTestConfig(paused: false);
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync("maintenance window", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        _repo.Verify(r => r.SaveSystemConfigAsync(
                It.Is<SystemConfig>(c =>
                    c.IsPromotionPaused &&
                    c.PauseReason == "maintenance window" &&
                    c.Version == config.Version + 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.PromotionPaused &&
                    t.OperatorReason == "maintenance window"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_WhenAlreadyPaused_IsIdempotent()
    {
        var config = CreateTestConfig(paused: true);
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync("another reason", CancellationToken.None);

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
        var result = await sut.ExecuteAsync("reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NO_CONFIGURATION");

        _repo.Verify(r => r.SaveSystemConfigAsync(
                It.IsAny<SystemConfig>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_DoesNotModifyRingParameters()
    {
        var config = CreateTestConfig(paused: false);
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var sut = CreateSut();
        await sut.ExecuteAsync("maintenance", CancellationToken.None);

        _repo.Verify(r => r.SaveRingStateAsync(
                It.IsAny<RingState>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
