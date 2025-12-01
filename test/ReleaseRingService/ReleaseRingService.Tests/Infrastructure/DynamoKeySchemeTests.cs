using FluentAssertions;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.DynamoDb.Keys;
using Xunit;

namespace ReleaseRingService.Tests.Infrastructure;

public class DynamoKeySchemeTests
{
    [Fact]
    public void SystemConfigKeys_AreConstantSingletonValues()
    {
        DynamoKeyScheme.SystemConfigPk.Should().Be("SYSTEM");
        DynamoKeyScheme.SystemConfigSk.Should().Be("CONFIG");
    }

    [Fact]
    public void CandidateKeys_UseAmiId()
    {
        var amiId = "ami-0abc123def456";

        var pk = DynamoKeyScheme.CandidatePk(amiId);
        var sk = DynamoKeyScheme.CandidateSk(amiId);

        pk.Should().Be("CANDIDATE#ami-0abc123def456");
        sk.Should().Be("CANDIDATE#ami-0abc123def456");
    }

    [Fact]
    public void RingStateKeys_UseRingName()
    {
        var ringName = "stable";

        var pk = DynamoKeyScheme.RingStatePk(ringName);
        var sk = DynamoKeyScheme.RingStateSk(ringName);

        pk.Should().Be("RING#stable");
        sk.Should().Be("RING#stable");
    }

    [Fact]
    public void TransitionKeys_UseRingNameAndTimestamp()
    {
        var ringName = "beta";
        var timestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        var ulid = "01HTEST123ABC";

        var pk = DynamoKeyScheme.TransitionPk(ringName);
        var sk = DynamoKeyScheme.TransitionSk(timestamp, ulid);

        pk.Should().Be("HISTORY#beta");
        sk.Should().StartWith("2026-05-22T12:00:00.0000000Z#");
        sk.Should().EndWith(ulid);
    }

    [Fact]
    public void TransitionSortKeys_AreLexicographicallyOrdered_ForSameRing()
    {
        var ulid1 = "01HAAAAAA001";
        var ulid2 = "01HAAAAAA002";

        var earlier = DynamoKeyScheme.TransitionSk(
            new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc), ulid1);
        var later = DynamoKeyScheme.TransitionSk(
            new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc), ulid2);

        string.CompareOrdinal(earlier, later).Should().BeNegative();
    }

    [Fact]
    public void Gsi1StatusKeys_EncodeStatusAndTimestamp()
    {
        var status = CandidateLifecycleStatus.Active;
        var timestamp = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

        var gsiPk = DynamoKeyScheme.Gsi1StatusPk(status);
        var gsiSk = DynamoKeyScheme.Gsi1StatusSk(timestamp);

        gsiPk.Should().Be("STATUS#Active");
        gsiSk.Should().Be("REGISTERED#2026-05-22T12:00:00.0000000Z");
    }

    [Fact]
    public void Gsi1Name_IsDefined()
    {
        DynamoKeyScheme.Gsi1Name.Should().Be("GSI1");
    }

    [Fact]
    public void AllRingKeys_AreDeterministic()
    {
        foreach (var ring in RingName.All)
        {
            var pk1 = DynamoKeyScheme.RingStatePk(ring.Value);
            var pk2 = DynamoKeyScheme.RingStatePk(ring.Value);
            pk1.Should().Be(pk2);
        }
    }
}
