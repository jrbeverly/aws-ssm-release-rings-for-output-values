using FluentAssertions;
using ReleaseRingService.Core.Models;
using Xunit;

namespace ReleaseRingService.Tests.Domain;

public class DomainVersionTests
{
    [Fact]
    public void SystemConfig_DefaultsVersionTo1()
    {
        var config = new SystemConfig
        {
            Region = "us-east-1",
            ParameterPrefix = "/test",
            Rings = RingName.All,
            SoakDurations = System.Collections.Immutable.ImmutableDictionary<string, TimeSpan>.Empty,
            IsPromotionPaused = false
        };

        config.Version.Should().Be(1);
    }

    [Fact]
    public void SystemConfig_CanOverrideVersion()
    {
        var config = new SystemConfig
        {
            Region = "us-east-1",
            ParameterPrefix = "/test",
            Rings = RingName.All,
            SoakDurations = System.Collections.Immutable.ImmutableDictionary<string, TimeSpan>.Empty,
            IsPromotionPaused = false,
            Version = 5
        };

        config.Version.Should().Be(5);
    }

    [Fact]
    public void RingState_DefaultsVersionTo1()
    {
        var state = new RingState
        {
            RingName = RingName.Nightly,
            CurrentAmiId = "ami-test",
            CurrentPublishedAt = System.DateTime.UtcNow
        };

        state.Version.Should().Be(1);
    }

    [Fact]
    public void RingState_CanOverrideVersion()
    {
        var state = new RingState
        {
            RingName = RingName.Nightly,
            CurrentAmiId = "ami-test",
            CurrentPublishedAt = System.DateTime.UtcNow,
            Version = 3
        };

        state.Version.Should().Be(3);
    }

    [Fact]
    public void Candidate_DefaultsVersionTo1()
    {
        var candidate = new Candidate
        {
            AmiId = "ami-test",
            RegistrationTimestamp = System.DateTime.UtcNow,
            SourceMetadata = "test",
            LifecycleStatus = CandidateLifecycleStatus.Active
        };

        candidate.Version.Should().Be(1);
    }

    [Fact]
    public void Candidate_CanOverrideVersion()
    {
        var candidate = new Candidate
        {
            AmiId = "ami-test",
            RegistrationTimestamp = System.DateTime.UtcNow,
            SourceMetadata = "test",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            Version = 10
        };

        candidate.Version.Should().Be(10);
    }

    [Fact]
    public void Version_IsPreservedInWithExpression()
    {
        var config = new SystemConfig
        {
            Region = "us-east-1",
            ParameterPrefix = "/test",
            Rings = RingName.All,
            SoakDurations = System.Collections.Immutable.ImmutableDictionary<string, TimeSpan>.Empty,
            IsPromotionPaused = false,
            Version = 4
        };

        var updated = config with { IsPromotionPaused = true };

        updated.Version.Should().Be(4);
        updated.IsPromotionPaused.Should().BeTrue();
    }
}
