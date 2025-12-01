using System.Collections.Immutable;
using FluentAssertions;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Verification.Harness;
using Xunit;

namespace ReleaseRingService.Verification.Workflows;

/// <summary>
/// Full-workflow verification of rollback and recovery: repointing a ring
/// to a previous AMI, auto-pause behaviour, and the full resume→reconcile
/// recovery path.
/// </summary>
public class RollbackAndRecoveryWorkflowTests
{
    private async Task<VerificationHarness> CreateBootstrappedHarnessAsync()
    {
        var harness = new VerificationHarness();
        await harness.BootstrapAsync();
        return harness;
    }

    [Fact]
    public async Task Rollback_RepointsRingToPreviousAmiAndAutoPauses()
    {
        using var harness = await CreateBootstrappedHarnessAsync();
        var now = DateTime.UtcNow;

        // Set up ring state where CurrentAmiId is "ami-00000000000000002" and PreviousAmiId is "ami-00000000000000001".
        var oldRingState = new RingState { RingName = RingName.Edge, CurrentAmiId = "ami-00000000000000002", PreviousAmiId = "ami-00000000000000001", CurrentPublishedAt = now, PreviousPublishedAt = now.AddDays(-7), Version = 3 };
        await harness.Repository.SaveRingStateAsync(oldRingState, CancellationToken.None);

        // Candidate exists (the old one was published to edge previously).
        var oldCandidate = new Candidate { AmiId = "ami-00000000000000001", RegistrationTimestamp = now.AddDays(-30), SourceMetadata = "old", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Edge, now.AddDays(-7)) };
        await harness.Repository.SaveCandidateAsync(oldCandidate, CancellationToken.None);

        var result = await harness.RollbackRing.ExecuteAsync(RingName.Edge, "ami-00000000000000001", "regression", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CurrentAmiId.Should().Be("ami-00000000000000001");
        result.Value.PreviousAmiId.Should().Be("ami-00000000000000002");

        // SSM parameter republished.
        var param = harness.GetPublishedParameter("/test/release-rings/amis/edge");
        param.Should().NotBeNull();
        param!.Value.Value.Should().Be("ami-00000000000000001");

        // Auto-pause applied.
        var config = await harness.Repository.GetSystemConfigAsync(CancellationToken.None);
        config!.IsPromotionPaused.Should().BeTrue();
        config.PauseReason.Should().Contain("ami-00000000000000001");

        // Transitions recorded.
        harness.Repository.AllTransitions().Should().Contain(t => t.Type == TransitionType.Rollback);
        harness.Repository.AllTransitions().Should().Contain(t => t.Type == TransitionType.PromotionPaused);
    }

    [Fact]
    public async Task RollbackWhenAlreadyPaused_DoesNotDuplicatePause()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync(paused: true);

        var now = DateTime.UtcNow;
        var ringState = new RingState { RingName = RingName.Beta, CurrentAmiId = "ami-00000000000000002", PreviousAmiId = "ami-00000000000000001", CurrentPublishedAt = now };
        await harness.Repository.SaveRingStateAsync(ringState, CancellationToken.None);

        var oldCandidate = new Candidate { AmiId = "ami-00000000000000001", RegistrationTimestamp = now.AddDays(-14), SourceMetadata = "old", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Beta, now.AddDays(-7)) };
        await harness.Repository.SaveCandidateAsync(oldCandidate, CancellationToken.None);

        var result = await harness.RollbackRing.ExecuteAsync(RingName.Beta, "ami-00000000000000001", "regression", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Only one pause transition (the pre-existing one).
        var pauseTransitions = harness.Repository.AllTransitions().Count(t => t.Type == TransitionType.PromotionPaused);
        pauseTransitions.Should().Be(0); // Bootstrapped with paused, no explicit pause transition
    }

    [Fact]
    public async Task RollbackToAlreadyCurrentAmi_ReturnsError()
    {
        using var harness = await CreateBootstrappedHarnessAsync();

        var ringState = new RingState { RingName = RingName.Stable, CurrentAmiId = "ami-1", CurrentPublishedAt = DateTime.UtcNow };
        await harness.Repository.SaveRingStateAsync(ringState, CancellationToken.None);

        var result = await harness.RollbackRing.ExecuteAsync(RingName.Stable, "ami-1", "reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("ALREADY_CURRENT");
    }

    [Fact]
    public async Task RollbackToAmiNotInRingHistory_ReturnsError()
    {
        using var harness = await CreateBootstrappedHarnessAsync();

        var ringState = new RingState { RingName = RingName.Stable, CurrentAmiId = "ami-1", CurrentPublishedAt = DateTime.UtcNow };
        await harness.Repository.SaveRingStateAsync(ringState, CancellationToken.None);

        // Candidate exists but was never published to stable.
        var unrelatedCandidate = new Candidate { AmiId = "ami-00000000000000004", RegistrationTimestamp = DateTime.UtcNow.AddDays(-7), SourceMetadata = "test", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, DateTime.UtcNow.AddDays(-7)) };
        await harness.Repository.SaveCandidateAsync(unrelatedCandidate, CancellationToken.None);

        var result = await harness.RollbackRing.ExecuteAsync(RingName.Stable, "ami-00000000000000004", "reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_IN_HISTORY");
    }

    [Fact]
    public async Task SsmPublishFailureDuringRollback_LeavesRingStateUnchanged()
    {
        using var harness = await CreateBootstrappedHarnessAsync();
        var now = DateTime.UtcNow;

        var ringState = new RingState { RingName = RingName.Stable, CurrentAmiId = "ami-00000000000000002", PreviousAmiId = "ami-00000000000000001", CurrentPublishedAt = now, PreviousPublishedAt = now.AddDays(-7), Version = 5 };
        await harness.Repository.SaveRingStateAsync(ringState, CancellationToken.None);

        var oldCandidate = new Candidate { AmiId = "ami-00000000000000001", RegistrationTimestamp = now.AddDays(-30), SourceMetadata = "old", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Stable, now.AddDays(-7)) };
        await harness.Repository.SaveCandidateAsync(oldCandidate, CancellationToken.None);

        harness.InjectSsmFailure();

        var result = await harness.RollbackRing.ExecuteAsync(RingName.Stable, "ami-00000000000000001", "regression", CancellationToken.None);

        result.IsFailure.Should().BeTrue();

        // Ring state is unchanged.
        var state = await harness.Repository.GetRingStateAsync(RingName.Stable, CancellationToken.None);
        state!.CurrentAmiId.Should().Be("ami-00000000000000002");

        // Only a PublishFailed transition is recorded (no state mutations).
        harness.Repository.AllTransitions()
            .Should().ContainSingle(t => t.Type == TransitionType.PublishFailed);
    }

    [Fact]
    public async Task FullRecoveryFlow_RollbackThenResumeThenReconcile()
    {
        using var harness = await CreateBootstrappedHarnessAsync();
        var now = DateTime.UtcNow;
        var longAgo = now.AddDays(-30);

        // Set up ring state with a previous AMI.
        var edgeState = new RingState { RingName = RingName.Edge, CurrentAmiId = "ami-00000000000000002", PreviousAmiId = "ami-00000000000000001", CurrentPublishedAt = now, PreviousPublishedAt = now.AddDays(-7) };
        await harness.Repository.SaveRingStateAsync(edgeState, CancellationToken.None);

        var oldCandidate = new Candidate { AmiId = "ami-00000000000000001", RegistrationTimestamp = now.AddDays(-60), SourceMetadata = "old", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, longAgo).Add(RingName.Edge, now.AddDays(-7)) };
        await harness.Repository.SaveCandidateAsync(oldCandidate, CancellationToken.None);

        // Another candidate waiting in nightly for edge.
        var waitingCandidate = new Candidate { AmiId = "ami-00000000000000003", RegistrationTimestamp = now.AddDays(-30), SourceMetadata = "wait", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, longAgo) };
        await harness.Repository.SaveCandidateAsync(waitingCandidate, CancellationToken.None);

        // 1. Rollback edge to ami-old → auto-pause.
        var rollback = await harness.RollbackRing.ExecuteAsync(RingName.Edge, "ami-00000000000000001", "regression detected", CancellationToken.None);
        rollback.IsSuccess.Should().BeTrue();

        var config = await harness.Repository.GetSystemConfigAsync(CancellationToken.None);
        config!.IsPromotionPaused.Should().BeTrue();

        // 2. Reconcile does nothing while paused.
        var reconcileDuringPause = await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        reconcileDuringPause.Value!.PromotedRings.Should().BeEmpty();

        // 3. Resume promotions.
        var resume = await harness.ResumePromotions.ExecuteAsync(CancellationToken.None);
        resume.IsSuccess.Should().BeTrue();

        config = await harness.Repository.GetSystemConfigAsync(CancellationToken.None);
        config!.IsPromotionPaused.Should().BeFalse();

        // 4. Reconcile now promotes the waiting candidate to edge.
        // But wait — the old candidate was just published to edge (by rollback), so the
        // waiting candidate should now be eligible (has nightly soak satisfied, not on edge yet).
        var reconcile = await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        reconcile.Value!.PromotedRings.Should().Contain("edge");

        // The waiting candidate gets promoted.
        var finalEdge = await harness.Repository.GetRingStateAsync(RingName.Edge, CancellationToken.None);
        finalEdge!.CurrentAmiId.Should().Be("ami-00000000000000003");
        finalEdge.PreviousAmiId.Should().Be("ami-00000000000000001");
    }
}
