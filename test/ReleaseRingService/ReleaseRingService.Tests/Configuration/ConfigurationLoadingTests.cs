using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ReleaseRingService.Core.Configuration;
using Xunit;

namespace ReleaseRingService.Tests.Configuration;

public class ConfigurationLoadingTests
{
    [Fact]
    public void Configuration_WithValidJson_BindsSuccessfully()
    {
        var json = """
        {
            "ReleaseRing": {
                "Region": "us-east-1",
                "ParameterPrefix": "/release-rings/amis",
                "Rings": ["nightly", "edge", "beta", "stable", "lts"],
                "SoakDurations": {
                    "nightly->edge": "01:00:00",
                    "edge->beta": "24:00:00",
                    "beta->stable": "72:00:00",
                    "stable->lts": "168:00:00"
                },
                "IsPromotionPaused": false
            }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var section = config.GetSection(ReleaseRingConfiguration.SectionName);
        var cfg = section.Get<ReleaseRingConfiguration>();

        cfg.Should().NotBeNull();
        cfg!.Region.Should().Be("us-east-1");
        cfg.ParameterPrefix.Should().Be("/release-rings/amis");
        cfg.Rings.Should().ContainInOrder("nightly", "edge", "beta", "stable", "lts");
        cfg.SoakDurations.Should().ContainKey("nightly->edge");
        cfg.SoakDurations["nightly->edge"].Should().Be("01:00:00");
        cfg.IsPromotionPaused.Should().BeFalse();
    }

    [Fact]
    public void Configuration_WithDefaults_UsesBuiltInDefaults()
    {
        var cfg = new ReleaseRingConfiguration
        {
            Region = "us-west-2"
        };

        cfg.Region.Should().Be("us-west-2");
        cfg.ParameterPrefix.Should().Be("/release-rings/amis");
        cfg.Rings.Should().ContainInOrder("nightly", "edge", "beta", "stable", "lts");
        cfg.TableName.Should().Be("ReleaseRingState");
    }
}
