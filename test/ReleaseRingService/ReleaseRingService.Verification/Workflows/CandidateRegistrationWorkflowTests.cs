using System.Collections.Immutable;
using FluentAssertions;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Verification.Harness;
using Xunit;

namespace ReleaseRingService.Verification.Workflows;

/// <summary>
/// Full-workflow verification of candidate registration, covering
/// net-new registration, idempotent re-registration, reactivation of
/// withdrawn/superseded candidates, and failure-safety when the SSM
/// publish step fails.
/// </summary>
public class CandidateRegistrationWorkflowTests
{
    private static readonly string ValidAmi = "ami-0abc123def4567890";

    [Fact]
    public async Task RegisterNewCandidate_PublishesToNightlyAndRecordsHistory()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        var result = await harness.RegisterCandidate.ExecuteAsync(
            ValidAmi, "ci-build/v1", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Outcome.Should().Be(RegistrationOutcome.Created);
        result.Value.Candidate.AmiId.Should().Be(ValidAmi);
        result.Value.Candidate.LifecycleStatus.Should().Be(CandidateLifecycleStatus.Active);
        result.Value.Candidate.SourceMetadata.Should().Be("ci-build/v1");
        result.Value.Candidate.PublicationTimestamps.Should().ContainKey(RingName.Nightly);

        // Nightly ring state created.
        var ringState = await harness.Repository.GetRingStateAsync(RingName.Nightly, CancellationToken.None);
        ringState.Should().NotBeNull();
        ringState!.CurrentAmiId.Should().Be(ValidAmi);
        ringState.PreviousAmiId.Should().BeNull();

        // Transition history recorded.
        var transitions = harness.Repository.AllTransitions();
        transitions.Should().Contain(t => t.Type == TransitionType.CandidateRegistered && t.AmiId == ValidAmi);
        transitions.Should().Contain(t => t.Type == TransitionType.RingPublication && t.RingName == RingName.Nightly);

        // SSM parameter published.
        var param = harness.GetPublishedParameter("/test/release-rings/amis/nightly");
        param.Should().NotBeNull();
        param!.Value.Value.Should().Be(ValidAmi);
    }

    [Fact]
    public async Task ReRegisterActiveCandidate_IsIdempotent()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        // First registration.
        var first = await harness.RegisterCandidate.ExecuteAsync(ValidAmi, null, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        first.Value!.Outcome.Should().Be(RegistrationOutcome.Created);

        var initialTransitionCount = harness.Repository.AllTransitions().Count;

        // Second registration of the same AMI.
        var second = await harness.RegisterCandidate.ExecuteAsync(ValidAmi, null, CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        second.Value!.Outcome.Should().Be(RegistrationOutcome.AlreadyKnown);
        second.Value.Candidate.AmiId.Should().Be(ValidAmi);

        // No additional transitions recorded.
        harness.Repository.AllTransitions().Count.Should().Be(initialTransitionCount);

        // SSM parameter published only once (version is 1).
        var param = harness.GetPublishedParameter("/test/release-rings/amis/nightly");
        param!.Value.Version.Should().Be(1);
    }

    [Fact]
    public async Task ReRegisterWithdrawnCandidate_ReactivatesIt()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        // Register then manually withdraw the candidate.
        await harness.RegisterCandidate.ExecuteAsync(ValidAmi, null, CancellationToken.None);
        var candidate = (await harness.Repository.GetCandidateAsync(ValidAmi, CancellationToken.None))!;
        await harness.Repository.SaveCandidateAsync(
            candidate with { LifecycleStatus = CandidateLifecycleStatus.Withdrawn },
            CancellationToken.None);

        // Re-register.
        var result = await harness.RegisterCandidate.ExecuteAsync(ValidAmi, null, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value!.Outcome.Should().Be(RegistrationOutcome.Created);
        result.Value.Candidate.LifecycleStatus.Should().Be(CandidateLifecycleStatus.Active);
    }

    [Fact]
    public async Task RegisterWithNullSourceMetadata_DefaultsToEmptyString()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        var result = await harness.RegisterCandidate.ExecuteAsync(ValidAmi, null, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value!.Candidate.SourceMetadata.Should().Be(string.Empty);
    }

    [Fact]
    public async Task PublishFailure_LeavesAuthoritativeStateUnchanged()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        harness.InjectSsmFailure();

        var result = await harness.RegisterCandidate.ExecuteAsync(ValidAmi, null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();

        // Candidate IS saved before publish attempt (by design).
        var candidate = await harness.Repository.GetCandidateAsync(ValidAmi, CancellationToken.None);
        candidate.Should().NotBeNull();

        // Ring state is NOT updated.
        var ringState = await harness.Repository.GetRingStateAsync(RingName.Nightly, CancellationToken.None);
        ringState.Should().BeNull();

        // Only a PublishFailed transition is recorded (no ring state mutations).
        harness.Repository.AllTransitions()
            .Should().ContainSingle(t => t.Type == TransitionType.PublishFailed);
        harness.Repository.AllTransitions()
            .Should().NotContain(t => t.Type == TransitionType.RingPublication);
    }
}
