using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Metrics;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Core.Ssm;
using ReleaseRingService.Infrastructure.Commands;
using ReleaseRingService.Infrastructure.DynamoDb;
using Xunit;

namespace ReleaseRingService.Tests.Commands;

public class RollbackRingCommandTests
{
    private readonly Mock<IDynamoDbRepository> _repo = new();
    private readonly Mock<IRingPublisher> _publisher = new();
    private readonly Mock<IMetricsPublisher> _metrics = new();

    private RollbackRingCommand CreateSut() =>
        new(_repo.Object, _publisher.Object, _metrics.Object,
            Mock.Of<ILogger<RollbackRingCommand>>());

    private static SystemConfig CreateTestConfig(bool paused = false) =>
        new()
        {
            Region = "us-east-1",
            ParameterPrefix = "/release-rings/amis",
            Rings = RingName.All,
            SoakDurations = ImmutableDictionary<string, TimeSpan>.Empty,
            IsPromotionPaused = paused,
            PauseReason = paused ? "previous-pause" : null
        };

    private static readonly string TargetAmi = "ami-0abc123def4567890";
    private static readonly string CurrentAmi = "ami-current";

    // ── Successful rollback ──────────────────────────────────────────────────

    [Fact]
    public async Task Execute_PublishesUpdatesRingStateAndRecordsHistory()
    {
        var config = CreateTestConfig();
        var now = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        var previousPublished = now.AddDays(-7);

        var currentRingState = new RingState
        {
            RingName = RingName.Stable,
            CurrentAmiId = CurrentAmi,
            PreviousAmiId = TargetAmi,
            CurrentPublishedAt = now,
            PreviousPublishedAt = previousPublished,
            Version = 5
        };

        var candidate = new Candidate
        {
            AmiId = TargetAmi,
            RegistrationTimestamp = now.AddDays(-14),
            SourceMetadata = "build-1",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty
                .Add(RingName.Stable, previousPublished)
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Stable, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentRingState);
        _repo.Setup(r => r.GetCandidateAsync(TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Stable, TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/stable",
                Ring = RingName.Stable,
                AmiId = TargetAmi,
                PublishedAt = now,
                ParameterVersion = 3
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(RingName.Stable, TargetAmi, "regression detected", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Published to SSM
        _publisher.Verify(p => p.PublishToRingAsync(
                RingName.Stable, TargetAmi, It.IsAny<CancellationToken>()),
            Times.Once);

        // Ring state updated
        _repo.Verify(r => r.SaveRingStateAsync(
                It.Is<RingState>(s =>
                    s.RingName == RingName.Stable &&
                    s.CurrentAmiId == TargetAmi &&
                    s.PreviousAmiId == CurrentAmi &&
                    s.PreviousPublishedAt == now &&
                    s.LastTransitionMetadata!.Contains("regression detected") &&
                    s.Version == 6),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Rollback transition recorded
        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.Rollback &&
                    t.RingName == RingName.Stable &&
                    t.AmiId == TargetAmi &&
                    t.OperatorReason == "regression detected"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Auto-pause transition recorded
        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.PromotionPaused &&
                    t.OperatorReason!.Contains("regression detected")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Auto-pause behaviour ─────────────────────────────────────────────────

    [Fact]
    public async Task Execute_SuccessfulRollback_AutoPauses()
    {
        var config = CreateTestConfig(paused: false);
        var now = DateTime.UtcNow;
        var currentRingState = new RingState
        {
            RingName = RingName.Edge,
            CurrentAmiId = CurrentAmi,
            PreviousAmiId = TargetAmi,
            CurrentPublishedAt = now
        };

        var candidate = new Candidate
        {
            AmiId = TargetAmi,
            RegistrationTimestamp = now.AddDays(-7),
            SourceMetadata = "build-1",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty
                .Add(RingName.Edge, now.AddDays(-1))
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Edge, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentRingState);
        _repo.Setup(r => r.GetCandidateAsync(TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Edge, TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/edge",
                Ring = RingName.Edge,
                AmiId = TargetAmi,
                PublishedAt = now,
                ParameterVersion = 2
            }));

        var sut = CreateSut();
        await sut.ExecuteAsync(RingName.Edge, TargetAmi, "regression", CancellationToken.None);

        _repo.Verify(r => r.SaveSystemConfigAsync(
                It.Is<SystemConfig>(c =>
                    c.IsPromotionPaused &&
                    c.PauseReason!.Contains("regression") &&
                    c.PauseReason!.Contains(TargetAmi) &&
                    c.Version == config.Version + 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_WhenAlreadyPaused_DoesNotDuplicatePause()
    {
        var config = CreateTestConfig(paused: true);
        var now = DateTime.UtcNow;
        var currentRingState = new RingState
        {
            RingName = RingName.Beta,
            CurrentAmiId = CurrentAmi,
            PreviousAmiId = TargetAmi,
            CurrentPublishedAt = now
        };

        var candidate = new Candidate
        {
            AmiId = TargetAmi,
            RegistrationTimestamp = now.AddDays(-14),
            SourceMetadata = "build-1",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty
                .Add(RingName.Beta, now.AddDays(-7))
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Beta, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentRingState);
        _repo.Setup(r => r.GetCandidateAsync(TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Beta, TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/beta",
                Ring = RingName.Beta,
                AmiId = TargetAmi,
                PublishedAt = now,
                ParameterVersion = 2
            }));

        var sut = CreateSut();
        await sut.ExecuteAsync(RingName.Beta, TargetAmi, "regression", CancellationToken.None);

        // Ring state and rollback transition still happen
        _repo.Verify(r => r.SaveRingStateAsync(
                It.IsAny<RingState>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // But no additional pause
        _repo.Verify(r => r.SaveSystemConfigAsync(
                It.IsAny<SystemConfig>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Exactly one PromotionPaused transition (the pre-existing one is already in the db)
        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t => t.Type == TransitionType.PromotionPaused),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Validation: not already current ─────────────────────────────────────

    [Fact]
    public async Task Execute_TargetIsAlreadyCurrent_ReturnsError()
    {
        var config = CreateTestConfig();
        var currentRingState = new RingState
        {
            RingName = RingName.Stable,
            CurrentAmiId = TargetAmi,
            CurrentPublishedAt = DateTime.UtcNow
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Stable, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentRingState);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(RingName.Stable, TargetAmi, "reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("ALREADY_CURRENT");

        _publisher.Verify(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Validation: target was previously published to this ring ─────────────

    [Fact]
    public async Task Execute_TargetNotPreviouslyPublishedToRing_ReturnsError()
    {
        var config = CreateTestConfig();
        var currentRingState = new RingState
        {
            RingName = RingName.Stable,
            CurrentAmiId = CurrentAmi,
            CurrentPublishedAt = DateTime.UtcNow
        };

        // Candidate exists but was never published to the stable ring
        var candidate = new Candidate
        {
            AmiId = TargetAmi,
            RegistrationTimestamp = DateTime.UtcNow.AddDays(-7),
            SourceMetadata = "build-1",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty
                .Add(RingName.Nightly, DateTime.UtcNow.AddDays(-7))
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Stable, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentRingState);
        _repo.Setup(r => r.GetCandidateAsync(TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(RingName.Stable, TargetAmi, "reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_IN_HISTORY");
    }

    [Fact]
    public async Task Execute_TargetIsPreviousAmi_AllowsEvenWithoutCandidatePubTimestamp()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var currentRingState = new RingState
        {
            RingName = RingName.Stable,
            CurrentAmiId = CurrentAmi,
            PreviousAmiId = TargetAmi,
            CurrentPublishedAt = now
        };

        // Candidate exists but with no publication to stable (edge case)
        var candidate = new Candidate
        {
            AmiId = TargetAmi,
            RegistrationTimestamp = now.AddDays(-14),
            SourceMetadata = "build-1",
            LifecycleStatus = CandidateLifecycleStatus.Active
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Stable, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentRingState);
        _repo.Setup(r => r.GetCandidateAsync(TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Stable, TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/stable",
                Ring = RingName.Stable,
                AmiId = TargetAmi,
                PublishedAt = now,
                ParameterVersion = 2
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(RingName.Stable, TargetAmi, "reason", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_CandidateNotFoundAndNotPrevious_ReturnsError()
    {
        var config = CreateTestConfig();
        var currentRingState = new RingState
        {
            RingName = RingName.Stable,
            CurrentAmiId = CurrentAmi,
            PreviousAmiId = "ami-some-other",
            CurrentPublishedAt = DateTime.UtcNow
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Stable, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentRingState);
        _repo.Setup(r => r.GetCandidateAsync(TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Candidate?)null);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(RingName.Stable, TargetAmi, "reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_IN_HISTORY");
    }

    // ── Validation: no ring state ───────────────────────────────────────────

    [Fact]
    public async Task Execute_RingHasNoState_ReturnsError()
    {
        var config = CreateTestConfig();
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Stable, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RingState?)null);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(RingName.Stable, TargetAmi, "reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("RING_NOT_FOUND");
    }

    // ── Validation: no configuration ────────────────────────────────────────

    [Fact]
    public async Task Execute_NoConfiguration_ReturnsError()
    {
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((SystemConfig?)null);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(RingName.Stable, TargetAmi, "reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NO_CONFIGURATION");
    }

    // ── SSM publish failure ─────────────────────────────────────────────────

    [Fact]
    public async Task Execute_SsmPublishFails_RecordsPublishFailedAndDoesNotUpdateRingState()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var currentRingState = new RingState
        {
            RingName = RingName.Stable,
            CurrentAmiId = CurrentAmi,
            PreviousAmiId = TargetAmi,
            CurrentPublishedAt = now
        };

        var candidate = new Candidate
        {
            AmiId = TargetAmi,
            RegistrationTimestamp = now.AddDays(-14),
            SourceMetadata = "build-1",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty
                .Add(RingName.Stable, now.AddDays(-7))
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Stable, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentRingState);
        _repo.Setup(r => r.GetCandidateAsync(TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Stable, TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Failure(
                new Error("SSM_PUBLISH_FAILED", "Service error")));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(RingName.Stable, TargetAmi, "reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("SSM_PUBLISH_FAILED");

        // Ring state must not change.
        _repo.Verify(r => r.SaveRingStateAsync(
                It.IsAny<RingState>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // PublishFailed transition must be recorded.
        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.PublishFailed &&
                    t.RingName == RingName.Stable &&
                    t.AmiId == TargetAmi),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Rollback to a candidate verified via PublicationTimestamps ───────────

    [Fact]
    public async Task Execute_TargetVerifiedViaCandidatePubTimestamps_Succeeds()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var currentRingState = new RingState
        {
            RingName = RingName.Edge,
            CurrentAmiId = CurrentAmi,
            PreviousAmiId = "ami-some-other-candidate",
            CurrentPublishedAt = now
        };

        // Candidate has publication timestamp for edge (not the previous AMI)
        var candidate = new Candidate
        {
            AmiId = TargetAmi,
            RegistrationTimestamp = now.AddDays(-30),
            SourceMetadata = "old-build",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty
                .Add(RingName.Nightly, now.AddDays(-30))
                .Add(RingName.Edge, now.AddDays(-20))
        };

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Edge, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentRingState);
        _repo.Setup(r => r.GetCandidateAsync(TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Edge, TargetAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/edge",
                Ring = RingName.Edge,
                AmiId = TargetAmi,
                PublishedAt = now,
                ParameterVersion = 2
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(RingName.Edge, TargetAmi, "rollback to old", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
