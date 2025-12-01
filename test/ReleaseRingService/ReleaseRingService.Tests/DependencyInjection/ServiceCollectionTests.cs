using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Configuration;
using ReleaseRingService.Infrastructure.Configuration;
using ReleaseRingService.Infrastructure.DynamoDb;
using ReleaseRingService.Infrastructure.Ec2;
using ReleaseRingService.Infrastructure.Ssm;
using Xunit;

namespace ReleaseRingService.Tests.DependencyInjection;

public class ServiceCollectionTests
{
    private static readonly IConfiguration EmptyConfig = new ConfigurationBuilder().Build();

    [Fact]
    public void Container_BuildsAndResolvesAllCommandInterfaces()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddReleaseRingService();
        services.AddReleaseRingInfrastructure(EmptyConfig);

        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRegisterCandidateCommand>().Should().NotBeNull();
        provider.GetRequiredService<IReconcileCommand>().Should().NotBeNull();
        provider.GetRequiredService<IRollbackRingCommand>().Should().NotBeNull();
        provider.GetRequiredService<IPausePromotionsCommand>().Should().NotBeNull();
        provider.GetRequiredService<IResumePromotionsCommand>().Should().NotBeNull();
        provider.GetRequiredService<IShowStateCommand>().Should().NotBeNull();
    }

    [Fact]
    public void Container_BuildsAndResolvesAllInfrastructureAdapters()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddReleaseRingService();
        services.AddReleaseRingInfrastructure(EmptyConfig);

        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDynamoDbRepository>().Should().NotBeNull();
        provider.GetRequiredService<ISsmParameterStore>().Should().NotBeNull();
        provider.GetRequiredService<IEc2ImageValidator>().Should().NotBeNull();
    }

    [Fact]
    public void Container_ResolvesRealShowStateCommand_NotStub()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddReleaseRingService();
        services.AddReleaseRingInfrastructure(EmptyConfig);

        var provider = services.BuildServiceProvider();
        var cmd = provider.GetRequiredService<IShowStateCommand>();

        cmd.Should().BeOfType<ReleaseRingService.Infrastructure.Commands.ShowStateCommand>();
    }

    [Fact]
    public void AddReleaseRingInfrastructure_RegistersAdapters()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddReleaseRingInfrastructure(EmptyConfig);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEc2ImageValidator>().Should().NotBeNull();
        provider.GetRequiredService<ISsmParameterStore>().Should().NotBeNull();
        provider.GetRequiredService<IDynamoDbRepository>().Should().NotBeNull();
    }
}
