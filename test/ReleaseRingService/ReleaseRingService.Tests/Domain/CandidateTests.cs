using System.Collections.Immutable;
using FluentAssertions;
using ReleaseRingService.Core.Models;
using Xunit;

namespace ReleaseRingService.Tests.Domain;

public class CandidateTests
{
    [Fact]
    public void Candidate_WithRequiredProperties_CreatesSuccessfully()
    {
        var candidate = new Candidate
        {
            AmiId = "ami-0abc123def456",
            RegistrationTimestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            SourceMetadata = "build-pipeline/v1.2.3",
            LifecycleStatus = CandidateLifecycleStatus.Active
        };

        candidate.AmiId.Should().Be("ami-0abc123def456");
        candidate.LifecycleStatus.Should().Be(CandidateLifecycleStatus.Active);
        candidate.PublicationTimestamps.Should().BeEmpty();
    }

    [Fact]
    public void Candidate_WithPublicationTimestamps_TracksPerRing()
    {
        var timestamps = ImmutableDictionary<RingName, DateTime>.Empty
            .Add(RingName.Nightly, new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc))
            .Add(RingName.Edge, new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc));

        var candidate = new Candidate
        {
            AmiId = "ami-0abc123def456",
            RegistrationTimestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            SourceMetadata = "build-pipeline/v1.2.3",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = timestamps
        };

        candidate.PublicationTimestamps.Should().HaveCount(2);
        candidate.PublicationTimestamps[RingName.Nightly]
            .Should().Be(new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Candidate_ValueEquality_ComparesAllProperties()
    {
        var c1 = new Candidate
        {
            AmiId = "ami-0abc123def456",
            RegistrationTimestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            SourceMetadata = "build-pipeline/v1.2.3",
            LifecycleStatus = CandidateLifecycleStatus.Active
        };

        var c2 = new Candidate
        {
            AmiId = "ami-0abc123def456",
            RegistrationTimestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            SourceMetadata = "build-pipeline/v1.2.3",
            LifecycleStatus = CandidateLifecycleStatus.Active
        };

        c1.Should().Be(c2);
    }
}
