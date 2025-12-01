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
using ReleaseRingService.Infrastructure.Ec2;
using Xunit;

namespace ReleaseRingService.Tests.Commands;

public class RegisterCandidateCommandTests
{
    private readonly Mock<IDynamoDbRepository> _repo = new();
    private readonly Mock<IRingPublisher> _publisher = new();
    private readonly Mock<IEc2ImageValidator> _ec2 = new();
    private readonly Mock<IMetricsPublisher> _metrics = new();

    private RegisterCandidateCommand CreateSut() =>
        new(_repo.Object, _publisher.Object, _ec2.Object, _metrics.Object,
            Mock.Of<ILogger<RegisterCandidateCommand>>());

    private static readonly string ValidAmi = "ami-0abc123def4567890";

    [Fact]
    public async Task Execute_NewCandidate_SavesCandidatePublishesToNightlyAndReturnsCreated()
    {
        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Candidate?)null);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/nightly",
                Ring = RingName.Nightly,
                AmiId = ValidAmi,
                PublishedAt = DateTime.UtcNow,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, "ci-build/v1", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Outcome.Should().Be(RegistrationOutcome.Created);
        result.Value.Candidate.AmiId.Should().Be(ValidAmi);
        result.Value.Candidate.LifecycleStatus.Should().Be(CandidateLifecycleStatus.Active);
        result.Value.Candidate.SourceMetadata.Should().Be("ci-build/v1");
        result.Value.Candidate.PublicationTimestamps.Should().ContainKey(RingName.Nightly);

        _repo.Verify(r => r.SaveCandidateAsync(
                It.Is<Candidate>(c => c.AmiId == ValidAmi),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _repo.Verify(r => r.SaveRingStateAsync(
                It.Is<RingState>(s => s.RingName == RingName.Nightly && s.CurrentAmiId == ValidAmi),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t => t.Type == TransitionType.CandidateRegistered),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t => t.Type == TransitionType.RingPublication),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_AlreadyKnownActiveCandidate_ReturnsAlreadyKnownWithoutMutation()
    {
        var existing = new Candidate
        {
            AmiId = ValidAmi,
            RegistrationTimestamp = DateTime.UtcNow.AddHours(-1),
            SourceMetadata = "ci-build/v1",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty
                .Add(RingName.Nightly, DateTime.UtcNow.AddHours(-1))
        };

        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Outcome.Should().Be(RegistrationOutcome.AlreadyKnown);
        result.Value.Candidate.Should().Be(existing);

        _publisher.Verify(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repo.Verify(r => r.SaveCandidateAsync(
                It.IsAny<Candidate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repo.Verify(r => r.SaveRingStateAsync(
                It.IsAny<RingState>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repo.Verify(r => r.AppendTransitionAsync(
                It.IsAny<TransitionRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_AmiNotFound_ReturnsAmiNotFoundError()
    {
        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("AMI_NOT_FOUND");
        result.Error.Message.Should().Contain(ValidAmi);

        _repo.Verify(r => r.GetCandidateAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _publisher.Verify(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_PublishFails_RecordsPublishFailedAndDoesNotUpdateRingState()
    {
        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Candidate?)null);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Failure(
                new Error("SSM_PUBLISH_FAILED", "Service error")));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("SSM_PUBLISH_FAILED");

        // Candidate is saved before publish attempt, but ring state is NOT updated.
        _repo.Verify(r => r.SaveCandidateAsync(
                It.IsAny<Candidate>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _repo.Verify(r => r.SaveRingStateAsync(
                It.IsAny<RingState>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // PublishFailed transition must be recorded.
        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.PublishFailed &&
                    t.RingName == RingName.Nightly &&
                    t.AmiId == ValidAmi),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_FirstEverRegistration_CreatesRingStateWithNoPrevious()
    {
        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Candidate?)null);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Nightly, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RingState?)null);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/nightly",
                Ring = RingName.Nightly,
                AmiId = ValidAmi,
                PublishedAt = DateTime.UtcNow,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _repo.Verify(r => r.SaveRingStateAsync(
                It.Is<RingState>(s =>
                    s.RingName == RingName.Nightly &&
                    s.CurrentAmiId == ValidAmi &&
                    s.PreviousAmiId == null &&
                    s.PreviousPublishedAt == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_SubsequentRegistration_PreservesPreviousRingState()
    {
        var previousTimestamp = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);
        var previousState = new RingState
        {
            RingName = RingName.Nightly,
            CurrentAmiId = "ami-old",
            CurrentPublishedAt = previousTimestamp,
            Version = 3
        };

        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Candidate?)null);
        _repo.Setup(r => r.GetRingStateAsync(RingName.Nightly, It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousState);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/nightly",
                Ring = RingName.Nightly,
                AmiId = ValidAmi,
                PublishedAt = DateTime.UtcNow,
                ParameterVersion = 2
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _repo.Verify(r => r.SaveRingStateAsync(
                It.Is<RingState>(s =>
                    s.RingName == RingName.Nightly &&
                    s.CurrentAmiId == ValidAmi &&
                    s.PreviousAmiId == "ami-old" &&
                    s.PreviousPublishedAt == previousTimestamp &&
                    s.Version == 4),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_WithdrawnCandidate_ReactivatesAndRepublishesToNightly()
    {
        var withdrawn = new Candidate
        {
            AmiId = ValidAmi,
            RegistrationTimestamp = DateTime.UtcNow.AddHours(-2),
            SourceMetadata = "ci-build/v1",
            LifecycleStatus = CandidateLifecycleStatus.Withdrawn
        };

        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(withdrawn);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/nightly",
                Ring = RingName.Nightly,
                AmiId = ValidAmi,
                PublishedAt = DateTime.UtcNow,
                ParameterVersion = 2
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Outcome.Should().Be(RegistrationOutcome.Created);
        result.Value.Candidate.LifecycleStatus.Should().Be(CandidateLifecycleStatus.Active);

        _repo.Verify(r => r.SaveCandidateAsync(
                It.Is<Candidate>(c => c.LifecycleStatus == CandidateLifecycleStatus.Active),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _publisher.Verify(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()),
            Times.Once);
        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.RingPublication &&
                    t.RingName == RingName.Nightly),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_NullSourceMetadata_UsesEmptyString()
    {
        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Candidate?)null);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/nightly",
                Ring = RingName.Nightly,
                AmiId = ValidAmi,
                PublishedAt = DateTime.UtcNow,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Candidate.SourceMetadata.Should().Be(string.Empty);
    }

    [Fact]
    public async Task Execute_TransitionRecords_ContainCorrectData()
    {
        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Candidate?)null);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/nightly",
                Ring = RingName.Nightly,
                AmiId = ValidAmi,
                PublishedAt = DateTime.UtcNow,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, "build-42", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.CandidateRegistered &&
                    t.AmiId == ValidAmi &&
                    t.Metadata == "build-42" &&
                    t.RingName == null),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.RingPublication &&
                    t.RingName == RingName.Nightly &&
                    t.AmiId == ValidAmi),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Publish failure transition recording ─────────────────────────────────

    [Fact]
    public async Task Execute_PublishFails_RecordsPublishFailedTransition()
    {
        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Candidate?)null);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Failure(
                new Error("SSM_PUBLISH_FAILED", "Service error")));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("SSM_PUBLISH_FAILED");

        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.PublishFailed &&
                    t.RingName == RingName.Nightly &&
                    t.AmiId == ValidAmi),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _repo.Verify(r => r.SaveRingStateAsync(
                It.IsAny<RingState>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Partial-state recovery ───────────────────────────────────────────────

    [Fact]
    public async Task Execute_ExistingActiveCandidateWithoutNightlyPub_ReattemptsPublish()
    {
        // Simulates recovery from a partial failure where the candidate was saved
        // to DynamoDB but the SSM publish never completed.
        var orphan = new Candidate
        {
            AmiId = ValidAmi,
            RegistrationTimestamp = DateTime.UtcNow.AddHours(-1),
            SourceMetadata = "ci-build/v1",
            LifecycleStatus = CandidateLifecycleStatus.Active
            // No PublicationTimestamps — nightly publish failed
        };

        _ec2.Setup(e => e.AmiExistsAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.GetCandidateAsync(ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orphan);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/nightly",
                Ring = RingName.Nightly,
                AmiId = ValidAmi,
                PublishedAt = DateTime.UtcNow,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Outcome.Should().Be(RegistrationOutcome.Created);

        // Must attempt to publish and record transitions.
        _publisher.Verify(p => p.PublishToRingAsync(
                RingName.Nightly, ValidAmi, It.IsAny<CancellationToken>()),
            Times.Once);

        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t => t.Type == TransitionType.CandidateRegistered),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t => t.Type == TransitionType.RingPublication),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _metrics.Verify(m => m.PromotionSucceeded(
                RingName.Nightly.Value, ValidAmi), Times.Once);
    }
}
