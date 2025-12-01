using Amazon.Lambda.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Commands;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ReleaseRingService.Worker;

public sealed class Function
{
    private readonly IServiceProvider _serviceProvider;

    public Function() : this(CreateServiceProvider())
    {
    }

    internal Function(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<string> HandleAsync(ILambdaContext context)
    {
        var logger = _serviceProvider.GetRequiredService<ILogger<Function>>();
        logger.LogInformation("Reconciliation worker invoked at {Timestamp}", DateTime.UtcNow);

        var reconcile = _serviceProvider.GetRequiredService<IReconcileCommand>();
        var result = await reconcile.ExecuteAsync(CancellationToken.None);

        if (result.IsSuccess)
        {
            var data = result.Value!;
            logger.LogInformation(
                "Reconciliation complete. Promoted: {PromotedCount}, Skipped: {SkippedCount}",
                data.PromotedRings.Count, data.SkippedRings.Count);
            return $"Promoted: {string.Join(", ", data.PromotedRings)}; " +
                   $"Skipped: {string.Join(", ", data.SkippedRings)}";
        }

        logger.LogError("Reconciliation failed: {Error}", result.Error);
        return $"Error: {result.Error}";
    }

    private static IServiceProvider CreateServiceProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(MicrosoftLogLevel.Information);
        });

        services.AddReleaseRingService();
        services.AddReleaseRingInfrastructure(configuration);

        return services.BuildServiceProvider();
    }
}

internal static class WorkerServiceCollectionExtensions
{
    internal static IServiceCollection AddReleaseRingService(
        this IServiceCollection services) =>
        Core.Configuration.ServiceCollectionExtensions.AddReleaseRingService(services);

    internal static IServiceCollection AddReleaseRingInfrastructure(
        this IServiceCollection services, IConfiguration configuration) =>
        Infrastructure.Configuration.ServiceCollectionExtensions.AddReleaseRingInfrastructure(services, configuration);
}
