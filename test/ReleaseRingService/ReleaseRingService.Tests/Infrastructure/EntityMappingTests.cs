using System.Collections.Immutable;
using FluentAssertions;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.DynamoDb.Entities;
using Xunit;

namespace ReleaseRingService.Tests.Infrastructure;

public class EntityMappingTests
{
    [Fact]
    public void SystemConfigEntity_RoundTrip_PreservesData()
    {
        var soakDurations = new Dictionary<string, TimeSpan>
        {
            ["nightly->edge"] = TimeSpan.FromHours(1),
            ["edge->beta"] = TimeSpan.FromHours(24),
            ["beta->stable"] = TimeSpan.FromHours(72),
            ["stable->lts"] = TimeSpan.FromHours(168)
        }.ToImmutableDictionary();

        var domain = new SystemConfig
        {
            Region = "us-east-1",
            ParameterPrefix = "/release-rings/amis",
            Rings = RingName.All,
            SoakDurations = soakDurations,
            IsPromotionPaused = false,
            PauseReason = null,
            Version = 1
        };

        var entity = SystemConfigEntity.FromDomain(domain);
        var roundTripped = entity.ToDomain();

        roundTripped.Region.Should().Be(domain.Region);
        roundTripped.ParameterPrefix.Should().Be(domain.ParameterPrefix);
        roundTripped.Rings.Should().BeEquivalentTo(domain.Rings);
        roundTripped.SoakDurations.Should().BeEquivalentTo(domain.SoakDurations);
        roundTripped.IsPromotionPaused.Should().Be(domain.IsPromotionPaused);
        roundTripped.PauseReason.Should().BeNull();
        roundTripped.Version.Should().Be(1);
    }

    [Fact]
    public void SystemConfigEntity_HasCorrectKeys()
    {
        var domain = new SystemConfig
        {
            Region = "us-east-1",
            ParameterPrefix = "/test",
            Rings = RingName.All,
            SoakDurations = ImmutableDictionary<string, TimeSpan>.Empty,
            IsPromotionPaused = false,
            Version = 3
        };

        var entity = SystemConfigEntity.FromDomain(domain);

        entity.PK.Should().Be("SYSTEM");
        entity.SK.Should().Be("CONFIG");
        entity.EntityType.Should().Be(DynamoDbEntityType.SystemConfig);
        entity.Version.Should().Be(3);
    }

    [Fact]
    public void CandidateEntity_RoundTrip_PreservesData()
    {
        var pubTimestamps = ImmutableDictionary<RingName, DateTime>.Empty
            .Add(RingName.Nightly, new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));

        var domain = new Candidate
        {
            AmiId = "ami-0abc123def456",
            RegistrationTimestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            SourceMetadata = "build-pipeline/v1.2.3",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            PublicationTimestamps = pubTimestamps
        };

        var entity = CandidateEntity.FromDomain(domain);
        var roundTripped = entity.ToDomain();

        roundTripped.AmiId.Should().Be(domain.AmiId);
        roundTripped.RegistrationTimestamp.Should().Be(domain.RegistrationTimestamp);
        roundTripped.SourceMetadata.Should().Be(domain.SourceMetadata);
        roundTripped.LifecycleStatus.Should().Be(domain.LifecycleStatus);
        roundTripped.PublicationTimestamps.Should().HaveCount(1);
        roundTripped.PublicationTimestamps[RingName.Nightly]
            .Should().Be(new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void CandidateEntity_HasCorrectKeys()
    {
        var domain = new Candidate
        {
            AmiId = "ami-0abc123def456",
            RegistrationTimestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            SourceMetadata = "test",
            LifecycleStatus = CandidateLifecycleStatus.Active,
            Version = 2
        };

        var entity = CandidateEntity.FromDomain(domain);

        entity.PK.Should().Be("CANDIDATE#ami-0abc123def456");
        entity.SK.Should().Be("CANDIDATE#ami-0abc123def456");
        entity.GSI1PK.Should().Be("STATUS#Active");
        entity.GSI1SK.Should().StartWith("REGISTERED#");
        entity.EntityType.Should().Be(DynamoDbEntityType.Candidate);
        entity.Version.Should().Be(2);
    }

    [Fact]
    public void CandidateEntity_Gsi1Sk_EncodesRegistrationTimestamp()
    {
        var timestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        var domain = new Candidate
        {
            AmiId = "ami-test",
            RegistrationTimestamp = timestamp,
            SourceMetadata = "test",
            LifecycleStatus = CandidateLifecycleStatus.Superseded
        };

        var entity = CandidateEntity.FromDomain(domain);

        entity.GSI1PK.Should().Be("STATUS#Superseded");
        entity.GSI1SK.Should().Contain("2026-05-22T12:00:00.0000000Z");
    }

    [Fact]
    public void RingStateEntity_RoundTrip_PreservesData()
    {
        var domain = new RingState
        {
            RingName = RingName.Stable,
            CurrentAmiId = "ami-current123",
            PreviousAmiId = "ami-previous456",
            CurrentPublishedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            PreviousPublishedAt = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
            LastTransitionMetadata = "auto-promotion from beta"
        };

        var entity = RingStateEntity.FromDomain(domain);
        var roundTripped = entity.ToDomain();

        roundTripped.RingName.Should().Be(RingName.Stable);
        roundTripped.CurrentAmiId.Should().Be("ami-current123");
        roundTripped.PreviousAmiId.Should().Be("ami-previous456");
        roundTripped.CurrentPublishedAt.Should().Be(domain.CurrentPublishedAt);
        roundTripped.PreviousPublishedAt.Should().Be(domain.PreviousPublishedAt);
        roundTripped.LastTransitionMetadata.Should().Be("auto-promotion from beta");
    }

    [Fact]
    public void RingStateEntity_HasCorrectKeys()
    {
        var domain = new RingState
        {
            RingName = RingName.Nightly,
            CurrentAmiId = "ami-test",
            CurrentPublishedAt = DateTime.UtcNow,
            Version = 7
        };

        var entity = RingStateEntity.FromDomain(domain);

        entity.PK.Should().Be("RING#nightly");
        entity.SK.Should().Be("RING#nightly");
        entity.EntityType.Should().Be(DynamoDbEntityType.RingState);
        entity.Version.Should().Be(7);
    }

    [Fact]
    public void TransitionRecordEntity_RoundTrip_PreservesData()
    {
        var domain = new TransitionRecord
        {
            Id = "01HTEST123ABC",
            Timestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            Type = TransitionType.RingPublication,
            RingName = RingName.Edge,
            AmiId = "ami-0abc123def456",
            Metadata = "auto-promotion",
            OperatorReason = null
        };

        var entity = TransitionRecordEntity.FromDomain(domain);
        var roundTripped = entity.ToDomain();

        roundTripped.Id.Should().Be("01HTEST123ABC");
        roundTripped.Timestamp.Should().Be(domain.Timestamp);
        roundTripped.Type.Should().Be(TransitionType.RingPublication);
        roundTripped.RingName.Should().Be(RingName.Edge);
        roundTripped.AmiId.Should().Be("ami-0abc123def456");
        roundTripped.Metadata.Should().Be("auto-promotion");
        roundTripped.OperatorReason.Should().BeNull();
    }

    [Fact]
    public void TransitionRecordEntity_HasCorrectKeys()
    {
        var timestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        var domain = new TransitionRecord
        {
            Id = "01HTEST123ABC",
            Timestamp = timestamp,
            Type = TransitionType.Rollback,
            RingName = RingName.Stable,
            AmiId = "ami-rollback",
            OperatorReason = "regression detected"
        };

        var entity = TransitionRecordEntity.FromDomain(domain);

        entity.PK.Should().Be("HISTORY#stable");
        entity.SK.Should().Contain("01HTEST123ABC");
        entity.EntityType.Should().Be(DynamoDbEntityType.TransitionRecord);
    }

    [Fact]
    public void TransitionRecordEntity_GlobalTransition_UsesGlobalRingName()
    {
        var domain = new TransitionRecord
        {
            Id = "01HTEST456DEF",
            Timestamp = DateTime.UtcNow,
            Type = TransitionType.PromotionPaused,
            RingName = null,
            OperatorReason = "maintenance window"
        };

        var entity = TransitionRecordEntity.FromDomain(domain);

        entity.PK.Should().Be("HISTORY#__global__");
        entity.RingName.Should().BeNull();
    }

    [Fact]
    public void CandidateEntity_EmptyPublicationTimestamps_RoundTrips()
    {
        var domain = new Candidate
        {
            AmiId = "ami-empty",
            RegistrationTimestamp = DateTime.UtcNow,
            SourceMetadata = "test",
            LifecycleStatus = CandidateLifecycleStatus.Active
        };

        var entity = CandidateEntity.FromDomain(domain);
        var roundTripped = entity.ToDomain();

        roundTripped.PublicationTimestamps.Should().BeEmpty();
    }

    [Fact]
    public void EntityTypeEnum_HasExpectedValues()
    {
        ((int)DynamoDbEntityType.SystemConfig).Should().Be(1);
        ((int)DynamoDbEntityType.Candidate).Should().Be(2);
        ((int)DynamoDbEntityType.RingState).Should().Be(3);
        ((int)DynamoDbEntityType.TransitionRecord).Should().Be(4);
    }
}
