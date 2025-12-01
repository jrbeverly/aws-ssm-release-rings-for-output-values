using System.Collections.Immutable;
using FluentAssertions;
using ReleaseRingService.Core.Models;
using Xunit;

namespace ReleaseRingService.Tests.Domain;

public class RingTransitionTests
{
    [Fact]
    public void BuildFromRingOrder_CreatesCorrectTransitions()
    {
        var rings = new List<RingName>(RingName.All);
        var soakDurations = new Dictionary<string, TimeSpan>
        {
            ["nightly->edge"] = TimeSpan.FromHours(1),
            ["edge->beta"] = TimeSpan.FromHours(24),
            ["beta->stable"] = TimeSpan.FromHours(72),
            ["stable->lts"] = TimeSpan.FromHours(168)
        };

        var transitions = RingTransition.BuildFromRingOrder(rings, soakDurations);

        transitions.Should().HaveCount(4);
        transitions[0].From.Should().Be(RingName.Nightly);
        transitions[0].To.Should().Be(RingName.Edge);
        transitions[0].SoakDuration.Should().Be(TimeSpan.FromHours(1));

        transitions[1].From.Should().Be(RingName.Edge);
        transitions[1].To.Should().Be(RingName.Beta);
        transitions[1].SoakDuration.Should().Be(TimeSpan.FromHours(24));

        transitions[2].From.Should().Be(RingName.Beta);
        transitions[2].To.Should().Be(RingName.Stable);
        transitions[2].SoakDuration.Should().Be(TimeSpan.FromHours(72));

        transitions[3].From.Should().Be(RingName.Stable);
        transitions[3].To.Should().Be(RingName.Lts);
        transitions[3].SoakDuration.Should().Be(TimeSpan.FromHours(168));
    }

    [Fact]
    public void BuildFromRingOrder_MissingDuration_DefaultsToZero()
    {
        var rings = new List<RingName> { RingName.Nightly, RingName.Edge };
        var emptyDurations = ImmutableDictionary<string, TimeSpan>.Empty;

        var transitions = RingTransition.BuildFromRingOrder(rings, emptyDurations);

        transitions.Should().HaveCount(1);
        transitions[0].SoakDuration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Key_FormatsCorrectly()
    {
        var transition = new RingTransition
        {
            From = RingName.Nightly,
            To = RingName.Edge,
            SoakDuration = TimeSpan.FromHours(1)
        };

        transition.Key.Should().Be("nightly->edge");
    }

    [Fact]
    public void BuildFromRingOrder_SingleRing_ReturnsEmpty()
    {
        var rings = new List<RingName> { RingName.Nightly };
        var soakDurations = ImmutableDictionary<string, TimeSpan>.Empty;

        var transitions = RingTransition.BuildFromRingOrder(rings, soakDurations);

        transitions.Should().BeEmpty();
    }

    [Fact]
    public void RingTransition_ValueEquality()
    {
        var t1 = new RingTransition
        {
            From = RingName.Nightly,
            To = RingName.Edge,
            SoakDuration = TimeSpan.FromHours(1)
        };
        var t2 = new RingTransition
        {
            From = RingName.Nightly,
            To = RingName.Edge,
            SoakDuration = TimeSpan.FromHours(1)
        };

        t1.Should().Be(t2);
    }
}
