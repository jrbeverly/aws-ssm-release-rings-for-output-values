using System.Collections.Immutable;
using FluentAssertions;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Verification.Harness;
using Xunit;

namespace ReleaseRingService.Verification.Workflows;

/// <summary>
/// Full-workflow verification of idempotency for all mutating commands.
/// Re-execution of each command must not produce duplicate state mutations
/// or duplicate transition history.
/// </summary>
public class IdempotencyWorkflowTests
{
    private static readonly string ValidAmi = "ami-0abc123def4567890";

    [Fact]
    public async Task RegisterCandidate_IdempotentReExecution()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        var first = await harness.RegisterCandidate.ExecuteAsync(ValidAmi, "build-1", CancellationToken.None);
        first.Value!.Outcome.Should().Be(RegistrationOutcome.Created);

        var second = await harness.RegisterCandidate.ExecuteAsync(ValidAmi, "build-1", CancellationToken.None);
        second.Value!.Outcome.Should().Be(RegistrationOutcome.AlreadyKnown);

        // The SSM parameter was published exactly once.
        var param = harness.GetPublishedParameter("/test/release-rings/amis/nightly");
        param!.Value.Version.Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_IdempotentReExecution_DoesNotDuplicatePromotions()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();
        var now = DateTime.UtcNow;
        var longAgo = now.AddDays(-30);

        var candidate = new Candidate { AmiId = ValidAmi, RegistrationTimestamp = now.AddDays(-90), SourceMetadata = "test", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = new[] { KeyValuePair.Create(RingName.Nightly, longAgo) }.ToImmutableDictionary() };
        await harness.Repository.SaveCandidateAsync(candidate, CancellationToken.None);

        // First reconciliation promotes to edge.
        await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        // Second reconciliation should not re-promote.
        var second = await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        second.Value!.PromotedRings.Should().BeEmpty();

        // Only one RingPublication transition for edge.
        var pubCount = harness.Repository.AllTransitions().Count(t => t.Type == TransitionType.RingPublication && t.RingName == RingName.Edge);
        pubCount.Should().Be(1);
    }

    [Fact]
    public async Task PausePromotions_IdempotentReExecution()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        await harness.PausePromotions.ExecuteAsync("reason 1", CancellationToken.None);
        await harness.PausePromotions.ExecuteAsync("reason 2", CancellationToken.None);

        var config = await harness.Repository.GetSystemConfigAsync(CancellationToken.None);
        config!.PauseReason.Should().Be("reason 1"); // Original reason preserved.

        var pauseCount = harness.Repository.AllTransitions().Count(t => t.Type == TransitionType.PromotionPaused);
        pauseCount.Should().Be(1);
    }

    [Fact]
    public async Task ResumePromotions_IdempotentReExecution()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync(paused: true);

        await harness.ResumePromotions.ExecuteAsync(CancellationToken.None);
        await harness.ResumePromotions.ExecuteAsync(CancellationToken.None);

        var resumeCount = harness.Repository.AllTransitions().Count(t => t.Type == TransitionType.PromotionResumed);
        resumeCount.Should().Be(1);
    }

    [Fact]
    public async Task RollbackRing_IdempotentReExecution()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();
        var now = DateTime.UtcNow;

        var ringState = new RingState { RingName = RingName.Edge, CurrentAmiId = "ami-00000000000000002", PreviousAmiId = ValidAmi, CurrentPublishedAt = now, PreviousPublishedAt = now.AddDays(-7) };
        await harness.Repository.SaveRingStateAsync(ringState, CancellationToken.None);

        var oldCandidate = new Candidate { AmiId = ValidAmi, RegistrationTimestamp = now.AddDays(-30), SourceMetadata = "old", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = new[] { KeyValuePair.Create(RingName.Edge, now.AddDays(-7)) }.ToImmutableDictionary() };
        await harness.Repository.SaveCandidateAsync(oldCandidate, CancellationToken.None);

        // First rollback succeeds.
        var first = await harness.RollbackRing.ExecuteAsync(RingName.Edge, ValidAmi, "regression", CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        // Second rollback to the SAME target (now current after first rollback) should fail.
        var second = await harness.RollbackRing.ExecuteAsync(RingName.Edge, ValidAmi, "retry", CancellationToken.None);
        second.IsFailure.Should().BeTrue();
        second.Error!.Code.Should().Be("ALREADY_CURRENT"); // Idempotent — no-op, not an error in state.

        // Only one rollback transition.
        var rollbackCount = harness.Repository.AllTransitions().Count(t => t.Type == TransitionType.Rollback);
        rollbackCount.Should().Be(1);
    }
}
