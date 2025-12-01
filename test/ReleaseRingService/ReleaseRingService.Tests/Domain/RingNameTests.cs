using FluentAssertions;
using ReleaseRingService.Core.Models;
using Xunit;

namespace ReleaseRingService.Tests.Domain;

public class RingNameTests
{
    [Fact]
    public void All_ContainsFiveFixedRingsInOrder()
    {
        RingName.All.Should().HaveCount(5);
        RingName.All[0].Value.Should().Be("nightly");
        RingName.All[1].Value.Should().Be("edge");
        RingName.All[2].Value.Should().Be("beta");
        RingName.All[3].Value.Should().Be("stable");
        RingName.All[4].Value.Should().Be("lts");
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("NIGHTLY")]
    [InlineData("Nightly")]
    [InlineData("edge")]
    [InlineData("beta")]
    [InlineData("stable")]
    [InlineData("lts")]
    public void From_WithValidRingName_ReturnsRing(string input)
    {
        var ring = RingName.From(input);
        ring.Should().NotBeNull();
        ring!.Value.Should().Be(input.ToLowerInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("production")]
    public void From_WithInvalidRingName_ReturnsNull(string input)
    {
        var ring = RingName.From(input);
        ring.Should().BeNull();
    }

    [Fact]
    public void RingName_SameValues_AreEqual()
    {
        var r1 = RingName.From("nightly");
        var r2 = RingName.From("nightly");

        r1.Should().Be(r2);
        r1.Should().Be(RingName.Nightly);
    }
}
