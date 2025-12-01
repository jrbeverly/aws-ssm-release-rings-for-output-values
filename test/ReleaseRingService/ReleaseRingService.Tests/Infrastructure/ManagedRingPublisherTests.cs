using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Core.Ssm;
using ReleaseRingService.Infrastructure.Ssm;
using Xunit;

namespace ReleaseRingService.Tests.Infrastructure;

public class ManagedRingPublisherTests
{
    private const string TestPrefix = "/release-rings/amis";

    private static ManagedRingPublisher CreateSut(
        Mock<IAmazonSimpleSystemsManagement>? ssmMock = null,
        string? parameterPrefix = null)
    {
        ssmMock ??= new Mock<IAmazonSimpleSystemsManagement>();
        return new ManagedRingPublisher(
            ssmMock.Object,
            parameterPrefix ?? TestPrefix,
            Mock.Of<ILogger<ManagedRingPublisher>>());
    }

    [Fact]
    public async Task PublishToRing_WithValidAmiId_ReturnsSuccessResult()
    {
        var ssm = new Mock<IAmazonSimpleSystemsManagement>();
        ssm.Setup(s => s.PutParameterAsync(
                It.IsAny<PutParameterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutParameterResponse
            {
                Version = 5,
                HttpStatusCode = System.Net.HttpStatusCode.OK
            });

        var sut = CreateSut(ssm);

        var result = await sut.PublishToRingAsync(RingName.Stable, "ami-0abc123def4567890", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Ring.Should().Be(RingName.Stable);
        result.Value.AmiId.Should().Be("ami-0abc123def4567890");
        result.Value.ParameterName.Should().Be("/release-rings/amis/stable");
        result.Value.ParameterVersion.Should().Be(5);
        result.Value.PublishedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublishToRing_UsesCorrectParameterTypeAndDataType()
    {
        PutParameterRequest? capturedRequest = null;
        var ssm = new Mock<IAmazonSimpleSystemsManagement>();
        ssm.Setup(s => s.PutParameterAsync(
                It.IsAny<PutParameterRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutParameterRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PutParameterResponse { Version = 1 });

        var sut = CreateSut(ssm);

        await sut.PublishToRingAsync(RingName.Nightly, "ami-00000000000000001", CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Type.Should().Be(ParameterType.String);
        capturedRequest.DataType.Should().Be("aws:ec2:image");
        capturedRequest.Overwrite.Should().BeTrue();
        capturedRequest.Name.Should().Be("/release-rings/amis/nightly");
        capturedRequest.Value.Should().Be("ami-00000000000000001");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ami")]
    [InlineData("ami-")]
    [InlineData("ami-abc")] // too short (7 hex chars)
    [InlineData("ami-0abc123def4567890g")] // 'g' not hex
    public async Task PublishToRing_WithInvalidAmiId_ReturnsInvalidAmiIdError(string? amiId)
    {
        var ssm = new Mock<IAmazonSimpleSystemsManagement>();
        var sut = CreateSut(ssm);

        var result = await sut.PublishToRingAsync(RingName.Edge, amiId!, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("INVALID_AMI_ID");
        ssm.Verify(s => s.PutParameterAsync(
                It.IsAny<PutParameterRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishToRing_WhenSsmThrows_ReturnsPublishFailedError()
    {
        var ssm = new Mock<IAmazonSimpleSystemsManagement>();
        ssm.Setup(s => s.PutParameterAsync(
                It.IsAny<PutParameterRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSimpleSystemsManagementException("Service error"));

        var sut = CreateSut(ssm);

        var result = await sut.PublishToRingAsync(RingName.Beta, "ami-0abc123def4567890", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("SSM_PUBLISH_FAILED");
        result.Error.Message.Should().Contain("/release-rings/amis/beta");
        result.Error.Message.Should().Contain("Service error");
    }

    [Fact]
    public async Task PublishToRing_ProducesParameterNameUnderConfiguredPrefix()
    {
        var ssm = new Mock<IAmazonSimpleSystemsManagement>();
        ssm.Setup(s => s.PutParameterAsync(
                It.IsAny<PutParameterRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutParameterRequest, CancellationToken>((req, _) => { })
            .ReturnsAsync(new PutParameterResponse { Version = 1 });

        var sut = CreateSut(ssm, "/custom/prefix");

        var result = await sut.PublishToRingAsync(RingName.Lts, "ami-12345678abcdef012", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ParameterName.Should().Be("/custom/prefix/lts");
    }

    [Fact]
    public async Task PublishToRing_TrimsTrailingSlashOnPrefix()
    {
        var ssm = new Mock<IAmazonSimpleSystemsManagement>();
        PutParameterRequest? capturedRequest = null;
        ssm.Setup(s => s.PutParameterAsync(
                It.IsAny<PutParameterRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutParameterRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PutParameterResponse { Version = 1 });

        var sut = CreateSut(ssm, "/release-rings/amis/");

        await sut.PublishToRingAsync(RingName.Stable, "ami-0abc123def4567890", CancellationToken.None);

        capturedRequest!.Name.Should().Be("/release-rings/amis/stable");
    }

    [Fact]
    public async Task PublishToRing_PublishesToAllRings()
    {
        var ssm = new Mock<IAmazonSimpleSystemsManagement>();
        ssm.Setup(s => s.PutParameterAsync(
                It.IsAny<PutParameterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutParameterResponse { Version = 1 });

        var sut = CreateSut(ssm);

        foreach (var ring in RingName.All)
        {
            var result = await sut.PublishToRingAsync(ring, "ami-0abc123def4567890", CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            result.Value!.Ring.Should().Be(ring);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithEmptyPrefix_ThrowsArgumentException(string prefix)
    {
        var act = () => new ManagedRingPublisher(
            new Mock<IAmazonSimpleSystemsManagement>().Object,
            prefix,
            Mock.Of<ILogger<ManagedRingPublisher>>());

        act.Should().Throw<ArgumentException>()
            .WithParameterName("parameterPrefix");
    }
}
