using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Metrics;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.DynamoDb;

namespace ReleaseRingService.Infrastructure.Commands;

public sealed class ResumePromotionsCommand : IResumePromotionsCommand
{
    private readonly IDynamoDbRepository _repository;
    private readonly IMetricsPublisher _metrics;
    private readonly ILogger<ResumePromotionsCommand> _logger;

    public ResumePromotionsCommand(
        IDynamoDbRepository repository,
        IMetricsPublisher metrics,
        ILogger<ResumePromotionsCommand> logger)
    {
        _repository = repository;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<Result> ExecuteAsync(CancellationToken ct)
    {
        var config = await _repository.GetSystemConfigAsync(ct);
        if (config is null)
        {
            return Result.Failure(new Error(
                "NO_CONFIGURATION",
                "System configuration has not been initialised."));
        }

        if (!config.IsPromotionPaused)
        {
            _logger.LogInformation("Promotions already active; no change");
            return Result.Success();
        }

        var now = DateTime.UtcNow;
        var updated = config with
        {
            IsPromotionPaused = false,
            PauseReason = null,
            Version = config.Version + 1
        };

        await _repository.SaveSystemConfigAsync(updated, ct);

        await _repository.AppendTransitionAsync(new TransitionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = now,
            Type = TransitionType.PromotionResumed
        }, ct);

        _logger.LogInformation(
            "Promotions resumed (previous pause reason: {PreviousReason})",
            config.PauseReason ?? "(none)");

        _metrics.PromotionsResumed();

        return Result.Success();
    }
}
