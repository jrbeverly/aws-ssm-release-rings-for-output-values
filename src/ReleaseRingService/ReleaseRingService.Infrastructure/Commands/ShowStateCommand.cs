using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Infrastructure.DynamoDb;

namespace ReleaseRingService.Infrastructure.Commands;

public sealed class ShowStateCommand : IShowStateCommand
{
    private const int RecentTransitionLimit = 20;

    private readonly IDynamoDbRepository _repository;
    private readonly ILogger<ShowStateCommand> _logger;

    public ShowStateCommand(
        IDynamoDbRepository repository,
        ILogger<ShowStateCommand> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result<ShowStateResult>> ExecuteAsync(CancellationToken ct)
    {
        var config = await _repository.GetSystemConfigAsync(ct);
        if (config is null)
        {
            return Result<ShowStateResult>.Failure(new Error(
                "NO_CONFIGURATION",
                "System configuration has not been initialised."));
        }

        var ringStates = await _repository.GetAllRingStatesAsync(ct);
        var recentTransitions = await _repository.GetRecentTransitionsAsync(
            RecentTransitionLimit, ct);

        _logger.LogDebug(
            "Show-state: {RingCount} rings, paused={Paused}, {TransitionCount} recent transitions",
            ringStates.Count, config.IsPromotionPaused, recentTransitions.Count);

        return Result<ShowStateResult>.Success(new ShowStateResult
        {
            RingStates = ringStates,
            IsPromotionPaused = config.IsPromotionPaused,
            PauseReason = config.PauseReason,
            RecentTransitions = recentTransitions
        });
    }
}
