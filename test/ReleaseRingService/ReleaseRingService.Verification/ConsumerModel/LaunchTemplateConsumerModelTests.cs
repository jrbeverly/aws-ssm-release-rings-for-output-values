using System.Collections.Immutable;
using FluentAssertions;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Verification.Harness;
using Xunit;

namespace ReleaseRingService.Verification.Workflows;

/// <summary>
/// Verifies the intended launch-template-based consumer model:
/// SSM parameters are published with correct naming, the full flow
/// from registration through promotion to consumption works, and
/// consumers can resolve the current ring AMI after each mutation.
/// </summary>
public class LaunchTemplateConsumerModelTests
{
    [Fact]
    public async Task ParametersPublishedUnderConfiguredPrefix()
    {
        using var harness = new VerificationHarness("/custom/prefix");
        await harness.BootstrapAsync();

        await harness.RegisterCandidate.ExecuteAsync("ami-0abc123def4567890", null, CancellationToken.None);

        harness.GetPublishedParameter("/custom/prefix/nightly").Should().NotBeNull();
    }

    [Fact]
    public async Task AllFiveRingsHaveParametersAfterFullFlow()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        var now = DateTime.UtcNow;
        var longAgo = now.AddDays(-365);

        // Register first — this publishes to nightly.
        await harness.RegisterCandidate.ExecuteAsync("ami-00000000000000005", null, CancellationToken.None);
        harness.GetPublishedParameter("/test/release-rings/amis/nightly").Should().NotBeNull();

        // Set nightly soak to long ago so edge promotion is immediately eligible.
        await RetroSetSoakAsync(harness, "ami-00000000000000005", RingName.Nightly, longAgo);

        // Reconcile: promotes to edge (nightly→edge soak satisfied).
        await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        // Set edge soak to long ago; reconcile promotes to beta.
        await RetroSetSoakAsync(harness, "ami-00000000000000005", RingName.Edge, longAgo);
        await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        // Set beta soak to long ago; reconcile promotes to stable.
        await RetroSetSoakAsync(harness, "ami-00000000000000005", RingName.Beta, longAgo);
        await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        // Set stable soak to long ago; reconcile promotes to lts.
        await RetroSetSoakAsync(harness, "ami-00000000000000005", RingName.Stable, longAgo);
        await harness.Reconcile.ExecuteAsync(CancellationToken.None);

        // All five ring parameters exist.
        foreach (var ring in new[] { "nightly", "edge", "beta", "stable", "lts" })
        {
            var param = harness.GetPublishedParameter($"/test/release-rings/amis/{ring}");
            param.Should().NotBeNull($"ring '{ring}' should have a published parameter");
        }
    }

    private static async Task RetroSetSoakAsync(
        VerificationHarness harness, string amiId, RingName ring, DateTime timestamp)
    {
        var candidate = (await harness.Repository.GetCandidateAsync(amiId, CancellationToken.None))!;
        candidate = candidate with
        {
            PublicationTimestamps = candidate.PublicationTimestamps.SetItem(ring, timestamp)
        };
        await harness.Repository.SaveCandidateAsync(candidate, CancellationToken.None);
    }

    [Fact]
    public async Task ConsumerResolvesCurrentAmiAfterPromotion()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();
        var now = DateTime.UtcNow;
        var longAgo = now.AddDays(-30);

        // Register candidate → publishes to nightly.
        await harness.RegisterCandidate.ExecuteAsync("ami-0abc123def4567890", null, CancellationToken.None);
        var nightlyParam = harness.GetPublishedParameter("/test/release-rings/amis/nightly");
        nightlyParam!.Value.Value.Should().Be("ami-0abc123def4567890");

        // Set up candidate for promotion to edge.
        var candidate = (await harness.Repository.GetCandidateAsync("ami-0abc123def4567890", CancellationToken.None))!;
        await harness.Repository.SaveCandidateAsync(candidate with { PublicationTimestamps = candidate.PublicationTimestamps.SetItem(RingName.Nightly, longAgo) }, CancellationToken.None);

        // Reconcile → promotes to edge.
        await harness.Reconcile.ExecuteAsync(CancellationToken.None);
        var edgeParam = harness.GetPublishedParameter("/test/release-rings/amis/edge");
        edgeParam.Should().NotBeNull();
        edgeParam!.Value.Value.Should().Be("ami-0abc123def4567890");
    }

    [Fact]
    public async Task ConsumerSeesUpdatedParameterAfterRollback()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();
        var now = DateTime.UtcNow;

        var ringState = new RingState { RingName = RingName.Edge, CurrentAmiId = "ami-00000000000000002", PreviousAmiId = "ami-00000000000000001", CurrentPublishedAt = now, PreviousPublishedAt = now.AddDays(-7) };
        await harness.Repository.SaveRingStateAsync(ringState, CancellationToken.None);

        var oldCandidate = new Candidate { AmiId = "ami-00000000000000001", RegistrationTimestamp = now.AddDays(-30), SourceMetadata = "old", LifecycleStatus = CandidateLifecycleStatus.Active, PublicationTimestamps = ImmutableDictionary<RingName, DateTime>.Empty.Add(RingName.Edge, now.AddDays(-7)) };
        await harness.Repository.SaveCandidateAsync(oldCandidate, CancellationToken.None);

        // Rollback republishes to edge.
        await harness.RollbackRing.ExecuteAsync(RingName.Edge, "ami-00000000000000001", "regression", CancellationToken.None);

        var edgeParam = harness.GetPublishedParameter("/test/release-rings/amis/edge");
        edgeParam!.Value.Value.Should().Be("ami-00000000000000001");
    }

    [Fact]
    public async Task AmiIdValidationRejectsInvalidIdentifiers()
    {
        using var harness = new VerificationHarness();
        await harness.BootstrapAsync();

        // Register with an invalid AMI ID — the ManagedRingPublisher validates the format.
        var result = await harness.RegisterCandidate.ExecuteAsync("not-an-ami", null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("INVALID_AMI_ID");

        // No parameter was published.
        harness.GetPublishedParameter("/test/release-rings/amis/nightly").Should().BeNull();
    }

    [Fact]
    public async Task ParameterUsesCorrectNamingPattern()
    {
        using var harness = new VerificationHarness("/release-rings/amis");
        await harness.BootstrapAsync();

        await harness.RegisterCandidate.ExecuteAsync("ami-0abc123def4567890", null, CancellationToken.None);

        // Verify naming pattern is /{prefix}/{ring}.
        harness.GetPublishedParameter("/release-rings/amis/nightly").Should().NotBeNull();
        harness.GetPublishedParameter("/release-rings/amis/edge").Should().BeNull(); // Not yet published.
    }
}
