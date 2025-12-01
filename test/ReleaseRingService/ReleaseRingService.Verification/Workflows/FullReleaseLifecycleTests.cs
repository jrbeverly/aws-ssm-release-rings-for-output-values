using System.Collections.Immutable;
using FluentAssertions;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Verification.Harness;
using Xunit;

namespace ReleaseRingService.Verification.Workflows;

/// <summary>
/// Single end-to-end test exercising the complete release lifecycle:
/// bootstrap → register → reconcile → promote through multiple rings →
/// rollback → auto-pause → resume → reconcile → show-state.
///
/// This test verifies that all commands work together as a cohesive system.
/// </summary>
public class FullReleaseLifecycleTests
{
    [Fact]
    public async Task CompleteReleaseLifecycle()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();
        var now = DateTime.UtcNow;

        // ── Phase 1: Register two candidates ───────────────────────────────
        var oldestAmi = "ami-00000000000000001";
        var newerAmi = "ami-00000000000000002";
        var longAgo = now.AddDays(-90);
        var recently = now.AddMinutes(-30);

        var r1 = await harness.RegisterCandidate.ExecuteAsync(oldestAmi, "build/1", CancellationToken.None);
        r1.IsSuccess.Should().BeTrue();
        r1.Value!.Outcome.Should().Be(RegistrationOutcome.Created);

        var r2 = await harness.RegisterCandidate.ExecuteAsync(newerAmi, "build/2", CancellationToken.None);
        r2.IsSuccess.Should().BeTrue();
        r2.Value!.Outcome.Should().Be(RegistrationOutcome.Created);

        // Both candidates published to nightly (latest wins).
        var nightlyParam = harness.GetPublishedParameter("/test/release-rings/amis/nightly");
        nightlyParam!.Value.Value.Should().Be(newerAmi);

        // ── Phase 2: Advance soak times so oldest candidate is eligible ────

        await AdvanceSoakForCandidateAsync(harness, oldestAmi, RingName.Nightly, longAgo);
        await AdvanceSoakForCandidateAsync(harness, newerAmi, RingName.Nightly, longAgo);

        // ── Phase 3: Reconcile → oldest promoted to edge ────────────────────
        var rec1 = await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        rec1.Value!.PromotedRings.Should().Contain("edge");

        var edgeState = await harness.Repository.GetRingStateAsync(RingName.Edge, CancellationToken.None);
        edgeState!.CurrentAmiId.Should().Be(oldestAmi);

        // ── Phase 4: Advance soak → oldest eligible for beta ───────────────
        // Prevent newer from being promoted to edge by pre-marking it as already on edge.
        await AdvanceSoakForCandidateAsync(harness, newerAmi, RingName.Edge, recently);
        await AdvanceSoakForCandidateAsync(harness, oldestAmi, RingName.Edge, longAgo);

        var rec2 = await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        rec2.Value!.PromotedRings.Should().Contain("beta");

        var betaState = await harness.Repository.GetRingStateAsync(RingName.Beta, CancellationToken.None);
        betaState!.CurrentAmiId.Should().Be(oldestAmi);

        // ── Phase 5: Rollback edge to an older AMI via previous ────────────

        // Set up a previous AMI on the edge ring state.
        var underlyingEdge = await harness.Repository.GetRingStateAsync(RingName.Edge, CancellationToken.None);
        var rolledBackEdge = underlyingEdge! with { PreviousAmiId = newerAmi, PreviousPublishedAt = now.AddDays(-50) };
        await harness.Repository.SaveRingStateAsync(rolledBackEdge, CancellationToken.None);

        // Make sure newerAmi has a publication record on edge.
        var newerCandidate = (await harness.Repository.GetCandidateAsync(newerAmi, CancellationToken.None))!;
        await harness.Repository.SaveCandidateAsync(
            newerCandidate with { PublicationTimestamps = newerCandidate.PublicationTimestamps.SetItem(RingName.Edge, now.AddDays(-50)) },
            CancellationToken.None);

        var rollback = await harness.RollbackRing.ExecuteAsync(RingName.Edge, newerAmi, "regression in oldest", CancellationToken.None);
        rollback.IsSuccess.Should().BeTrue();

        // Edge now points to newerAmi.
        var edgeAfterRollback = await harness.Repository.GetRingStateAsync(RingName.Edge, CancellationToken.None);
        edgeAfterRollback!.CurrentAmiId.Should().Be(newerAmi);

        // Auto-pause is active.
        var config = await harness.Repository.GetSystemConfigAsync(CancellationToken.None);
        config!.IsPromotionPaused.Should().BeTrue();

        // ── Phase 6: Reconcile does nothing while paused ────────────────────
        var recDuringPause = await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        recDuringPause.Value!.PromotedRings.Should().BeEmpty();

        // ── Phase 7: Resume promotions ─────────────────────────────────────
        var resume = await harness.ResumePromotions.ExecuteAsync(CancellationToken.None);
        resume.IsSuccess.Should().BeTrue();

        config = await harness.Repository.GetSystemConfigAsync(CancellationToken.None);
        config!.IsPromotionPaused.Should().BeFalse();

        // ── Phase 8: Show-state confirms final state ────────────────────────
        var showState = await harness.ShowState.ExecuteAsync(CancellationToken.None);
        showState.IsSuccess.Should().BeTrue();
        showState.Value!.IsPromotionPaused.Should().BeFalse();
        showState.Value.RingStates.Should().HaveCountGreaterOrEqualTo(3); // nightly, edge, beta at minimum

        // Verify all transitions are coherent.
        var transitions = harness.Repository.AllTransitions();
        transitions.Should().Contain(t => t.Type == TransitionType.CandidateRegistered && t.AmiId == oldestAmi);
        transitions.Should().Contain(t => t.Type == TransitionType.CandidateRegistered && t.AmiId == newerAmi);
        transitions.Should().Contain(t => t.Type == TransitionType.Rollback && t.RingName == RingName.Edge);
        transitions.Should().Contain(t => t.Type == TransitionType.PromotionPaused);
        transitions.Should().Contain(t => t.Type == TransitionType.PromotionResumed);
    }

    private static async Task AdvanceSoakForCandidateAsync(
        VerificationHarness harness, string amiId, RingName ring, DateTime timestamp)
    {
        var candidate = (await harness.Repository.GetCandidateAsync(amiId, CancellationToken.None))!;
        var updatedPubs = candidate.PublicationTimestamps.SetItem(ring, timestamp);
        await harness.Repository.SaveCandidateAsync(
            candidate with { PublicationTimestamps = updatedPubs },
            CancellationToken.None);
    }
}
