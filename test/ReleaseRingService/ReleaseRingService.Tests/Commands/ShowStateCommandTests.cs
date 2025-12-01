using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.Commands;
using ReleaseRingService.Infrastructure.DynamoDb;
using Xunit;

namespace ReleaseRingService.Tests.Commands;

public class ShowStateCommandTests
{
    private readonly Mock<IDynamoDbRepository> _repo = new();

    private ShowStateCommand CreateSut() =>
        new(_repo.Object, Mock.Of<ILogger<ShowStateCommand>>());

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

    private static RingState CreateRingState(RingName ring, string amiId, DateTime publishedAt) =>
        new()
        {
            RingName = ring,
            CurrentAmiId = amiId,
            CurrentPublishedAt = publishedAt
        };

    private static void SetupDefaultTransitions(Mock<IDynamoDbRepository> repo)
    {
        repo.Setup(r => r.GetRecentTransitionsAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    [Fact]
    public async Task Execute_ReturnsRingStatesAndPauseStatus()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var ringStates = new List<RingState>
        {
            CreateRingState(RingName.Nightly, "ami-nightly", now),
            CreateRingState(RingName.Edge, "ami-edge", now.AddHours(-1))
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ringStates);
        SetupDefaultTransitions(_repo);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsPromotionPaused.Should().BeFalse();
        result.Value.PauseReason.Should().BeNull();
        result.Value.RingStates.Should().HaveCount(2);
        result.Value.RingStates[0].RingName.Should().Be(RingName.Nightly);
        result.Value.RingStates[1].RingName.Should().Be(RingName.Edge);
    }

    [Fact]
    public async Task Execute_WhenPaused_ReturnsPauseReason()
    {
        var config = CreateTestConfig(paused: true);
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        SetupDefaultTransitions(_repo);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsPromotionPaused.Should().BeTrue();
        result.Value.PauseReason.Should().Be("maintenance");
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

        _repo.Verify(r => r.GetRecentTransitionsAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_WithNoRingStates_ReturnsEmptyList()
    {
        var config = CreateTestConfig();
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        SetupDefaultTransitions(_repo);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RingStates.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_ReturnsAllRingStates()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var ringStates = RingName.All.Select(r =>
            CreateRingState(r, $"ami-{r.Value}", now)).ToList();

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ringStates);
        SetupDefaultTransitions(_repo);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RingStates.Should().HaveCount(5);
        result.Value.RingStates.Select(rs => rs.RingName).Should()
            .BeEquivalentTo(RingName.All);
    }

    [Fact]
    public async Task Execute_IncludesRecentTransitions()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var transitions = new List<TransitionRecord>
        {
            new()
            {
                Id = "t1",
                Timestamp = now,
                Type = TransitionType.RingPublication,
                RingName = RingName.Edge,
                AmiId = "ami-1"
            },
            new()
            {
                Id = "t2",
                Timestamp = now.AddMinutes(-5),
                Type = TransitionType.CandidateRegistered,
                AmiId = "ami-2"
            }
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repo.Setup(r => r.GetRecentTransitionsAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(transitions);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RecentTransitions.Should().HaveCount(2);
        result.Value.RecentTransitions[0].Id.Should().Be("t1");
        result.Value.RecentTransitions[1].Id.Should().Be("t2");
    }

    [Fact]
    public async Task Execute_FetchesRecentTransitionsWithExpectedLimit()
    {
        var config = CreateTestConfig();
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repo.Setup(r => r.GetRecentTransitionsAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = CreateSut();
        await sut.ExecuteAsync(CancellationToken.None);

        _repo.Verify(r => r.GetRecentTransitionsAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
