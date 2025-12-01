using FluentAssertions;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Verification.Harness;
using Xunit;

namespace ReleaseRingService.Verification.Workflows;

/// <summary>
/// Full-workflow verification of pause and resume semantics:
/// idempotent execution, reconciler behaviour under pause, and
/// correct transition history.
/// </summary>
public class PauseResumeWorkflowTests
{
    [Fact]
    public async Task Pause_PreventsReconciliation()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        var pause = await harness.PausePromotions.ExecuteAsync("maintenance window", CancellationToken.None);
        pause.IsSuccess.Should().BeTrue();

        var config = await harness.Repository.GetSystemConfigAsync(CancellationToken.None);
        config!.IsPromotionPaused.Should().BeTrue();
        config.PauseReason.Should().Be("maintenance window");

        // Reconcile exits without mutation.
        var reconcile = await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        reconcile.Value!.PromotedRings.Should().BeEmpty();

        // Transition recorded.
        harness.Repository.AllTransitions().Should().Contain(t => t.Type == TransitionType.PromotionPaused);
    }

    [Fact]
    public async Task PauseWhenAlreadyPaused_IsIdempotent()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync(paused: true);

        var pause = await harness.PausePromotions.ExecuteAsync("second reason", CancellationToken.None);
        pause.IsSuccess.Should().BeTrue();

        // Reason unchanged.
        var config = await harness.Repository.GetSystemConfigAsync(CancellationToken.None);
        config!.PauseReason.Should().Be("bootstrapped-paused");

        // No additional transition (the bootstrap doesn't record a transition).
        var pauseTransitions = harness.Repository.AllTransitions().Count(t => t.Type == TransitionType.PromotionPaused);
        pauseTransitions.Should().Be(0);
    }

    [Fact]
    public async Task Resume_AfterPause_ReenablesReconciliation()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync(paused: true);

        var resume = await harness.ResumePromotions.ExecuteAsync(CancellationToken.None);
        resume.IsSuccess.Should().BeTrue();

        var config = await harness.Repository.GetSystemConfigAsync(CancellationToken.None);
        config!.IsPromotionPaused.Should().BeFalse();
        config.PauseReason.Should().BeNull();

        harness.Repository.AllTransitions().Should().Contain(t => t.Type == TransitionType.PromotionResumed);
    }

    [Fact]
    public async Task ResumeWhenAlreadyActive_IsIdempotent()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync(paused: false);

        var resume = await harness.ResumePromotions.ExecuteAsync(CancellationToken.None);
        resume.IsSuccess.Should().BeTrue();

        // No transition recorded.
        harness.Repository.AllTransitions().Count(t => t.Type == TransitionType.PromotionResumed).Should().Be(0);
    }

    [Fact]
    public async Task FullPauseResumeCycle_PreservesRingState()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        // Set up some ring state before pause.
        var ringState = new RingState { RingName = RingName.Edge, CurrentAmiId = "ami-00000000000000001", CurrentPublishedAt = DateTime.UtcNow };
        await harness.Repository.SaveRingStateAsync(ringState, CancellationToken.None);

        // Pause.
        await harness.PausePromotions.ExecuteAsync("reason", CancellationToken.None);

        // Resume.
        await harness.ResumePromotions.ExecuteAsync(CancellationToken.None);

        // Ring state is intact.
        var state = await harness.Repository.GetRingStateAsync(RingName.Edge, CancellationToken.None);
        state!.CurrentAmiId.Should().Be("ami-00000000000000001");
    }
}
