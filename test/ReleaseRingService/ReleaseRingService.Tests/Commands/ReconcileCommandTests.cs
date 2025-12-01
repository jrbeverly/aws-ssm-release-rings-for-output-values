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

public class ReconcileCommandTests
{
    private readonly Mock<IDynamoDbRepository> _repo = new();
    private readonly Mock<IRingPublisher> _publisher = new();
    private readonly Mock<IMetricsPublisher> _metrics = new();

    private ReconcileCommand CreateSut() =>
        new(_repo.Object, _publisher.Object, _metrics.Object,
            Mock.Of<ILogger<ReconcileCommand>>());

    private static SystemConfig CreateTestConfig(bool paused = false) =>
        new()
        {
            Region = "us-east-1",
            ParameterPrefix = "/release-rings/amis",
            Rings = RingName.All,
            SoakDurations = new Dictionary<string, TimeSpan>
            {
                ["nightly->edge"] = TimeSpan.FromHours(1),
                ["edge->beta"] = TimeSpan.FromHours(24),
                ["beta->stable"] = TimeSpan.FromHours(72),
                ["stable->lts"] = TimeSpan.FromHours(168)
            }.ToImmutableDictionary(),
            IsPromotionPaused = paused
        };

    private static Candidate CreateCandidate(
        string amiId,
        DateTime registeredAt,
        params (RingName ring, DateTime publishedAt)[] publications)
    {
        var timestamps = ImmutableDictionary<RingName, DateTime>.Empty;
        foreach (var (ring, publishedAt) in publications)
            timestamps = timestamps.Add(ring, publishedAt);

        return new Candidate
        {
            AmiId = amiId,
            RegistrationTimestamp = registeredAt,
            SourceMetadata = "test",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = timestamps
        };
    }

    private static RingState CreateRingState(RingName ring, string currentAmi, DateTime publishedAt)
    {
        return new RingState
        {
            RingName = ring,
            CurrentAmiId = currentAmi,
            CurrentPublishedAt = publishedAt,
            Version = 1
        };
    }

    // ── Pause exit ────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_WhenPromotionsPaused_ExitsWithoutMutation()
    {
        var config = CreateTestConfig(paused: true);
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PromotedRings.Should().BeEmpty();
        result.Value.SkippedRings.Should().BeEquivalentTo(
            ["edge", "beta", "stable", "lts"]);

        _repo.Verify(r => r.GetCandidatesByStatusAsync(
                It.IsAny<CandidateLifecycleStatus>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _publisher.Verify(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── No configuration ──────────────────────────────────────────────────

    [Fact]
    public async Task Execute_WhenNoConfiguration_ReturnsError()
    {
        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((SystemConfig?)null);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NO_CONFIGURATION");
    }

    // ── Oldest eligible candidate promotion ───────────────────────────────

    [Fact]
    public async Task Execute_PromotesOldestEligibleCandidateToEdge()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var soaked = now.AddHours(-2); // 2h > 1h soak

        var oldest = CreateCandidate("ami-oldest", now.AddDays(-7),
            (RingName.Nightly, soaked));
        var newer = CreateCandidate("ami-newer", now.AddDays(-1),
            (RingName.Nightly, soaked)); // also soaked, but newer

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([oldest, newer]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Edge, "ami-oldest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/edge",
                Ring = RingName.Edge,
                AmiId = "ami-oldest",
                PublishedAt = now,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PromotedRings.Should().Contain("edge");

        _publisher.Verify(p => p.PublishToRingAsync(
                RingName.Edge, "ami-oldest", It.IsAny<CancellationToken>()),
            Times.Once);
        _publisher.Verify(p => p.PublishToRingAsync(
                RingName.Edge, "ami-newer", It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Soak duration enforcement ─────────────────────────────────────────

    [Fact]
    public async Task Execute_SkipsRingWhenSoakDurationNotMet()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var notSoaked = now.AddMinutes(-30); // 30 min < 1h required

        var candidate = CreateCandidate("ami-1", now.AddDays(-2),
            (RingName.Nightly, notSoaked));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PromotedRings.Should().BeEmpty();
        result.Value.SkippedRings.Should().Contain("edge");

        _publisher.Verify(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── One candidate per ring per run ────────────────────────────────────

    [Fact]
    public async Task Execute_PromotesAtMostOneCandidatePerRing()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var soaked = now.AddHours(-3);

        var c1 = CreateCandidate("ami-1", now.AddDays(-5),
            (RingName.Nightly, soaked));
        var c2 = CreateCandidate("ami-2", now.AddDays(-4),
            (RingName.Nightly, soaked));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([c1, c2]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Edge, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/edge",
                Ring = RingName.Edge,
                AmiId = "ami-1",
                PublishedAt = now,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _publisher.Verify(p => p.PublishToRingAsync(
                RingName.Edge, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Failed publish preserves state ────────────────────────────────────

    [Fact]
    public async Task Execute_WhenPublishFails_RecordsFailureAndLeavesRingStateUnchanged()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var soaked = now.AddHours(-3);

        var candidate = CreateCandidate("ami-1", now.AddDays(-3),
            (RingName.Nightly, soaked));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Edge, "ami-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Failure(
                new Error("SSM_PUBLISH_FAILED", "Service error")));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.SkippedRings.Should().Contain("edge");

        _repo.Verify(r => r.SaveRingStateAsync(
                It.IsAny<RingState>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repo.Verify(r => r.SaveCandidateAsync(
                It.IsAny<Candidate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.PublishFailed &&
                    t.RingName == RingName.Edge &&
                    t.AmiId == "ami-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Ring state updates correctly on promotion ─────────────────────────

    [Fact]
    public async Task Execute_OnPromotion_UpdatesRingStateWithPreviousAndCurrent()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var soaked = now.AddHours(-3);
        var previousPublished = now.AddDays(-30);

        var candidate = CreateCandidate("ami-1", now.AddDays(-3),
            (RingName.Nightly, soaked));

        var existingEdgeState = CreateRingState(RingName.Edge, "ami-old", previousPublished);

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([existingEdgeState]);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Edge, "ami-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/edge",
                Ring = RingName.Edge,
                AmiId = "ami-1",
                PublishedAt = now,
                ParameterVersion = 2
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        _repo.Verify(r => r.SaveRingStateAsync(
                It.Is<RingState>(s =>
                    s.RingName == RingName.Edge &&
                    s.CurrentAmiId == "ami-1" &&
                    s.PreviousAmiId == "ami-old" &&
                    s.PreviousPublishedAt == previousPublished &&
                    s.Version == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Candidate publication timestamp recorded ──────────────────────────

    [Fact]
    public async Task Execute_OnPromotion_RecordsPublicationTimestampOnCandidate()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var soaked = now.AddHours(-3);

        var candidate = CreateCandidate("ami-1", now.AddDays(-3),
            (RingName.Nightly, soaked));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _publisher.Setup(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/edge",
                Ring = RingName.Edge,
                AmiId = "ami-1",
                PublishedAt = now,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        await sut.ExecuteAsync(CancellationToken.None);

        _repo.Verify(r => r.SaveCandidateAsync(
                It.Is<Candidate>(c =>
                    c.AmiId == "ami-1" &&
                    c.PublicationTimestamps.ContainsKey(RingName.Edge) &&
                    c.PublicationTimestamps.ContainsKey(RingName.Nightly)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Transition history for successful promotion ───────────────────────

    [Fact]
    public async Task Execute_OnPromotion_RecordsRingPublicationTransition()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var soaked = now.AddHours(-3);

        var candidate = CreateCandidate("ami-1", now.AddDays(-3),
            (RingName.Nightly, soaked));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _publisher.Setup(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/edge",
                Ring = RingName.Edge,
                AmiId = "ami-1",
                PublishedAt = now,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        await sut.ExecuteAsync(CancellationToken.None);

        _repo.Verify(r => r.AppendTransitionAsync(
                It.Is<TransitionRecord>(t =>
                    t.Type == TransitionType.RingPublication &&
                    t.RingName == RingName.Edge &&
                    t.AmiId == "ami-1" &&
                    t.Metadata!.Contains("auto-promotion")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Candidate must be in previous ring to be eligible ─────────────────

    [Fact]
    public async Task Execute_SkipsCandidateNotYetPublishedToPreviousRing()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;

        // Candidate registered but never published to any ring
        var candidate = CreateCandidate("ami-1", now.AddDays(-7));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PromotedRings.Should().BeEmpty();
        result.Value.SkippedRings.Should().Contain("edge");

        _publisher.Verify(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Already-published candidate is not re-promoted ────────────────────

    [Fact]
    public async Task Execute_SkipsCandidateAlreadyPublishedToTargetRing()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var soaked = now.AddHours(-3);

        // Candidate already published to both nightly AND edge
        var candidate = CreateCandidate("ami-1", now.AddDays(-7),
            (RingName.Nightly, soaked),
            (RingName.Edge, soaked));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PromotedRings.Should().BeEmpty();

        _publisher.Verify(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Multi-ring promotion (one ring at a time) ─────────────────────────

    [Fact]
    public async Task Execute_PromotesCandidateToOnlyOneRingPerRun()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var longAgo = now.AddDays(-30);

        var candidate = CreateCandidate("ami-1", now.AddDays(-90),
            (RingName.Nightly, longAgo));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _publisher.Setup(p => p.PublishToRingAsync(
                It.IsAny<RingName>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/ring",
                Ring = RingName.Edge,
                AmiId = "ami-1",
                PublishedAt = now,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PromotedRings.Should().Contain("edge");
        result.Value.PromotedRings.Should().NotContain("beta");
    }

    // ── No candidates at all ──────────────────────────────────────────────

    [Fact]
    public async Task Execute_WithNoActiveCandidates_SkipsAllRings()
    {
        var config = CreateTestConfig();

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PromotedRings.Should().BeEmpty();
        result.Value.SkippedRings.Should().BeEquivalentTo(
            ["edge", "beta", "stable", "lts"]);
    }

    // ── First ever promotion to a ring (no prior ring state) ──────────────

    [Fact]
    public async Task Execute_FirstPromotionToRing_CreatesRingStateWithNoPrevious()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var soaked = now.AddHours(-3);

        var candidate = CreateCandidate("ami-1", now.AddDays(-3),
            (RingName.Nightly, soaked));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Edge, "ami-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/edge",
                Ring = RingName.Edge,
                AmiId = "ami-1",
                PublishedAt = now,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        _repo.Verify(r => r.SaveRingStateAsync(
                It.Is<RingState>(s =>
                    s.RingName == RingName.Edge &&
                    s.CurrentAmiId == "ami-1" &&
                    s.PreviousAmiId == null &&
                    s.PreviousPublishedAt == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Superseded/Withdrawn candidates filtered at GSI level ─────────────

    [Fact]
    public async Task Execute_OnlyProcessesCandidatesReturnedByStatusQuery()
    {
        var config = CreateTestConfig();
        var now = DateTime.UtcNow;
        var soaked = now.AddHours(-3);

        // Only Active candidates are returned by GetCandidatesByStatusAsync(Active)
        var active = CreateCandidate("ami-active", now.AddDays(-5),
            (RingName.Nightly, soaked));

        _repo.Setup(r => r.GetSystemConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _repo.Setup(r => r.GetCandidatesByStatusAsync(
                CandidateLifecycleStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync([active]);
        _repo.Setup(r => r.GetAllRingStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _publisher.Setup(p => p.PublishToRingAsync(
                RingName.Edge, "ami-active", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = "/prefix/edge",
                Ring = RingName.Edge,
                AmiId = "ami-active",
                PublishedAt = now,
                ParameterVersion = 1
            }));

        var sut = CreateSut();
        var result = await sut.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _publisher.Verify(p => p.PublishToRingAsync(
                RingName.Edge, "ami-active", It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
