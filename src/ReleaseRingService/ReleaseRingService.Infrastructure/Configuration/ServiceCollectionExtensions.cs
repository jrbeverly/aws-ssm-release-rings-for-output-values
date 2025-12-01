using Amazon;
using Amazon.DynamoDBv2;
using Amazon.SimpleSystemsManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Configuration;
using ReleaseRingService.Core.Metrics;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Core.Ssm;
using ReleaseRingService.Infrastructure.Commands;
using ReleaseRingService.Infrastructure.DynamoDb;
using ReleaseRingService.Infrastructure.Ec2;
using ReleaseRingService.Infrastructure.Metrics;
using ReleaseRingService.Infrastructure.Ssm;

namespace ReleaseRingService.Infrastructure.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReleaseRingInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var ringConfig = configuration
            .GetSection(ReleaseRingConfiguration.SectionName)
            .Get<ReleaseRingConfiguration>() ?? new ReleaseRingConfiguration();

        // AWS SDK clients
        services.AddSingleton<IAmazonSimpleSystemsManagement>(_ =>
        {
            if (!string.IsNullOrWhiteSpace(ringConfig.Region))
                return new AmazonSimpleSystemsManagementClient(
                    RegionEndpoint.GetBySystemName(ringConfig.Region));

            return new AmazonSimpleSystemsManagementClient();
        });

        services.AddSingleton<IAmazonDynamoDB>(_ =>
        {
            if (!string.IsNullOrWhiteSpace(ringConfig.Region))
                return new AmazonDynamoDBClient(
                    RegionEndpoint.GetBySystemName(ringConfig.Region));

            return new AmazonDynamoDBClient();
        });

        // Ring publisher — validates and publishes to managed ring parameters
        services.AddSingleton<IRingPublisher>(sp =>
        {
            var ssm = sp.GetRequiredService<IAmazonSimpleSystemsManagement>();
            var logger = sp.GetRequiredService<ILogger<ManagedRingPublisher>>();
            return new ManagedRingPublisher(ssm, ringConfig.ParameterPrefix, logger);
        });

        // General SSM adapter
        services.AddSingleton<ISsmParameterStore>(sp =>
        {
            var ssm = sp.GetRequiredService<IAmazonSimpleSystemsManagement>();
            return new AwsSsmParameterStore(ssm);
        });

        // DynamoDB repository (system of record)
        services.AddSingleton<IDynamoDbRepository>(sp =>
        {
            var dynamoDb = sp.GetRequiredService<IAmazonDynamoDB>();
            return new DynamoDbRepository(dynamoDb, ringConfig.TableName);
        });

        // Metrics publisher — emits baseline operational signals as structured logs.
        services.AddSingleton<IMetricsPublisher, LoggingMetricsPublisher>();

        // Command implementations — override stubs registered by Core.
        services.AddTransient<IRegisterCandidateCommand, RegisterCandidateCommand>();
        services.AddTransient<IReconcileCommand, ReconcileCommand>();
        services.AddTransient<IRollbackRingCommand, RollbackRingCommand>();
        services.AddTransient<IPausePromotionsCommand, PausePromotionsCommand>();
        services.AddTransient<IResumePromotionsCommand, ResumePromotionsCommand>();
        services.AddTransient<IShowStateCommand, ShowStateCommand>();

        // Remaining adapters still stubbed
        services.AddSingleton<IEc2ImageValidator, StubEc2ImageValidator>();

        return services;
    }
}

file sealed class StubEc2ImageValidator : IEc2ImageValidator
{
    public Task<bool> AmiExistsAsync(string amiId, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<bool> IsValidImageReferenceAsync(string amiId, CancellationToken ct) =>
        Task.FromResult(true);
}
