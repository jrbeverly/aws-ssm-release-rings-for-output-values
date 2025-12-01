using System.Collections.Immutable;
using FluentAssertions;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Verification.Harness;
using Xunit;

namespace ReleaseRingService.Verification.Workflows;

/// <summary>
/// Full-workflow verification of deterministic promotion reconciliation:
/// oldest-eligible-first ordering, soak-duration enforcement, one
/// candidate per ring per run, and correct ring state transitions.
/// </summary>
public class PromotionReconciliationWorkflowTests
{
    private async Task<VerificationHarness> CreateBootstrappedHarnessAsync()
    {
        var harness = new VerificationHarness();
        await harness.BootstrapAsync();
        return harness;
    }

    [Fact]
    public async Task PromotesOldestEligibleCandidateToEachRing()
    {
        using var harness = await CreateBootstrappedHarnessAsync();
        var now = DateTime.UtcNow;
        var longAgo = now.AddDays(-30);

        // Two candidates: one old, one new. Both published to nightly long ago.
        var oldest = new Candidate { AmiId = "ami-00000000000000001", RegistrationTimestamp = now.AddDays(-90), SourceMetadata = "old", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, longAgo) };
        var newer = new Candidate { AmiId = "ami-00000000000000002", RegistrationTimestamp = now.AddDays(-60), SourceMetadata = "new", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, longAgo) };
        await harness.Repository.SaveCandidateAsync(oldest, CancellationToken.None);
        await harness.Repository.SaveCandidateAsync(newer, CancellationToken.None);

        var result = await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // The oldest candidate should be promoted to edge.
        result.Value!.PromotedRings.Should().Contain("edge");

        var edgeState = await harness.Repository.GetRingStateAsync(RingName.Edge, CancellationToken.None);
        edgeState.Should().NotBeNull();
        edgeState!.CurrentAmiId.Should().Be("ami-00000000000000001");

        // Newer candidate should NOT be promoted yet (one per ring).
        result.Value.PromotedRings.Should().NotContain("beta");
    }

    [Fact]
    public async Task SkipsCandidateWhenSoakDurationNotMet()
    {
        using var harness = await CreateBootstrappedHarnessAsync();
        var now = DateTime.UtcNow;
        var justNow = now.AddMinutes(-5); // 5 min < 1 hour required soak

        var candidate = new Candidate { AmiId = "ami-00000000000000003", RegistrationTimestamp = now.AddDays(-2), SourceMetadata = "test", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, justNow) };
        await harness.Repository.SaveCandidateAsync(candidate, CancellationToken.None);

        var result = await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PromotedRings.Should().BeEmpty();
        result.Value.SkippedRings.Should().Contain("edge");
    }

    [Fact]
    public async Task SkipsCandidateAlreadyPublishedToTargetRing()
    {
        using var harness = await CreateBootstrappedHarnessAsync();
        var now = DateTime.UtcNow;
        var longAgo = now.AddDays(-30);
        var recently = now.AddMinutes(-30); // 30 min ago, < 24h edge→beta soak

        // Published to nightly long ago; published to edge very recently so
        // edge→beta soak is not yet satisfied.
        var candidate = new Candidate { AmiId = "ami-00000000000000003", RegistrationTimestamp = now.AddDays(-90), SourceMetadata = "test", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, longAgo).Add(RingName.Edge, recently) };
        await harness.Repository.SaveCandidateAsync(candidate, CancellationToken.None);

        var result = await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        // Candidate already on edge — skipped for edge.
        // Edge→beta soak is 24h, but edge was published only 30 min ago — skipped for beta.
        result.Value!.PromotedRings.Should().BeEmpty();
    }

    [Fact]
    public async Task SkipsCandidateNotPublishedToPreviousRing()
    {
        using var harness = await CreateBootstrappedHarnessAsync();
        var now = DateTime.UtcNow;

        // Registered but never published to nightly.
        var candidate = new Candidate { AmiId = "ami-00000000000000003", RegistrationTimestamp = now.AddDays(-7), SourceMetadata = "test", LifecycleStatus = CandidateLifecycleStatus.Active };
        await harness.Repository.SaveCandidateAsync(candidate, CancellationToken.None);

        var result = await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        result.Value!.PromotedRings.Should().BeEmpty();
        result.Value.SkippedRings.Should().Contain("edge");
    }

    [Fact]
    public async Task ExitsWithoutMutationWhenPromotionsPaused()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync(paused: true);

        var now = DateTime.UtcNow;
        var candidate = new Candidate { AmiId = "ami-00000000000000003", RegistrationTimestamp = now.AddDays(-7), SourceMetadata = "test", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, now.AddDays(-6)) };
        await harness.Repository.SaveCandidateAsync(candidate, CancellationToken.None);

        var result = await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.PromotedRings.Should().BeEmpty();
        // All target rings skipped.
        result.Value.SkippedRings.Should().BeEquivalentTo(["edge", "beta", "stable", "lts"]);

        // No ring state mutations.
        var edge = await harness.Repository.GetRingStateAsync(RingName.Edge, CancellationToken.None);
        edge.Should().BeNull();
    }

    [Fact]
    public async Task PublishFailure_RecordsFailureAndLeavesRingStateUnchanged()
    {
        using var harness = await CreateBootstrappedHarnessAsync();
        var now = DateTime.UtcNow;
        var longAgo = now.AddDays(-30);

        var candidate = new Candidate { AmiId = "ami-00000000000000003", RegistrationTimestamp = now.AddDays(-90), SourceMetadata = "test", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, longAgo) };
        await harness.Repository.SaveCandidateAsync(candidate, CancellationToken.None);

        harness.InjectSsmFailure();

        var result = await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue(); // Reconcile itself succeeds even when a promotion fails
        result.Value!.SkippedRings.Should().Contain("edge");

        // Ring state is NOT mutated.
        var edge = await harness.Repository.GetRingStateAsync(RingName.Edge, CancellationToken.None);
        edge.Should().BeNull();

        // PublishFailed transition recorded.
        harness.Repository.AllTransitions().Should().Contain(t => t.Type == TransitionType.PublishFailed && t.RingName == RingName.Edge);
    }

    [Fact]
    public async Task MultiRingPromotion_AdvancesOneCandidatePerReconciliation()
    {
        using var harness = await CreateBootstrappedHarnessAsync();
        var now = DateTime.UtcNow;
        var longAgo = now.AddDays(-90);

        // Candidate soaked through all rings.
        var candidate = new Candidate { AmiId = "ami-00000000000000003", RegistrationTimestamp = longAgo, SourceMetadata = "test", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Nightly, longAgo) };
        await harness.Repository.SaveCandidateAsync(candidate, CancellationToken.None);

        // First reconciliation: promoted to edge.
        var r1 = await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        r1.Value!.PromotedRings.Should().Contain("edge");
        r1.Value.PromotedRings.Should().NotContain("beta");

        // Second reconciliation: not yet eligible for beta (edge→beta soak is 24h, edge publish just happened).
        var r2 = await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        r2.Value!.PromotedRings.Should().BeEmpty();
    }
}
