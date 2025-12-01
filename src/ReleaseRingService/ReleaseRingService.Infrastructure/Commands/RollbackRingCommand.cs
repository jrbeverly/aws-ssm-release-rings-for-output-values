using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Metrics;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Core.Ssm;
using ReleaseRingService.Infrastructure.DynamoDb;

namespace ReleaseRingService.Infrastructure.Commands;

public sealed class RollbackRingCommand : IRollbackRingCommand
{
    private readonly IDynamoDbRepository _repository;
    private readonly IRingPublisher _ringPublisher;
    private readonly IMetricsPublisher _metrics;
    private readonly ILogger<RollbackRingCommand> _logger;

    public RollbackRingCommand(
        IDynamoDbRepository repository,
        IRingPublisher ringPublisher,
        IMetricsPublisher metrics,
        ILogger<RollbackRingCommand> logger)
    {
        _repository = repository;
        _ringPublisher = ringPublisher;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<Result<RingState>> ExecuteAsync(
        RingName ringName, string targetAmiId, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // 1. Load system configuration.
        var config = await _repository.GetSystemConfigAsync(ct);
        if (config is null)
        {
            return Result<RingState>.Failure(new Error(
                "NO_CONFIGURATION",
                "System configuration has not been initialised."));
        }

        // 2. Load current ring state for the target ring.
        var currentRingState = await _repository.GetRingStateAsync(ringName, ct);
        if (currentRingState is null)
        {
            return Result<RingState>.Failure(new Error(
                "RING_NOT_FOUND",
                $"Ring '{ringName.Value}' has no recorded state. A ring must have a published AMI before it can be rolled back."));
        }

        // 3. Validate that the target is not already the current AMI.
        if (currentRingState.CurrentAmiId == targetAmiId)
        {
            return Result<RingState>.Failure(new Error(
                "ALREADY_CURRENT",
                $"AMI '{targetAmiId}' is already the current publication for ring '{ringName.Value}'."));
        }

        // 4. Validate that the target AMI was previously published to this ring.
        var targetCandidate = await _repository.GetCandidateAsync(targetAmiId, ct);
        var wasPreviouslyPublished =
            currentRingState.PreviousAmiId == targetAmiId ||
            targetCandidate?.PublicationTimestamps.ContainsKey(ringName) == true;

        if (!wasPreviouslyPublished)
        {
            _logger.LogWarning(
                "Rollback rejected: {AmiId} was not previously published to ring {Ring}",
                targetAmiId, ringName.Value);
            return Result<RingState>.Failure(new Error(
                "NOT_IN_HISTORY",
                $"AMI '{targetAmiId}' was not previously published to ring '{ringName.Value}'. Rollback targets must come from the selected ring's own publication history."));
        }

        // 5. Republishes the ring parameter to the chosen AMI.
        var publishResult = await _ringPublisher.PublishToRingAsync(ringName, targetAmiId, ct);
        if (publishResult.IsFailure)
        {
            _logger.LogError(
                "SSM publish failed during rollback of ring {Ring} to {AmiId}: {Error}",
                ringName.Value, targetAmiId, publishResult.Error);

            await _repository.AppendTransitionAsync(new TransitionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = now,
                Type = TransitionType.PublishFailed,
                RingName = ringName,
                AmiId = targetAmiId,
                Metadata = publishResult.Error?.ToString()
            }, ct);

            return Result<RingState>.Failure(publishResult.Error!);
        }

        // 6. Update ring state — move current to previous, set target as current.
        var newRingState = currentRingState with
        {
            PreviousAmiId = currentRingState.CurrentAmiId,
            PreviousPublishedAt = currentRingState.CurrentPublishedAt,
            CurrentAmiId = targetAmiId,
            CurrentPublishedAt = now,
            LastTransitionMetadata = $"operator-rollback: {reason}",
            Version = currentRingState.Version + 1
        };

        await _repository.SaveRingStateAsync(newRingState, ct);

        // 7. Record transition history for the rollback.
        await _repository.AppendTransitionAsync(new TransitionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = now,
            Type = TransitionType.Rollback,
            RingName = ringName,
            AmiId = targetAmiId,
            OperatorReason = reason
        }, ct);

        // 8. Pause automatic promotion if not already paused.
        if (!config.IsPromotionPaused)
        {
            var pauseReason =
                $"Auto-paused after rollback of {ringName.Value} to {targetAmiId}: {reason}";

            var updatedConfig = config with
            {
                IsPromotionPaused = true,
                PauseReason = pauseReason,
                Version = config.Version + 1
            };
            await _repository.SaveSystemConfigAsync(updatedConfig, ct);

            await _repository.AppendTransitionAsync(new TransitionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = now,
                Type = TransitionType.PromotionPaused,
                OperatorReason = pauseReason
            }, ct);

            _logger.LogInformation(
                "Promotions auto-paused after rollback of {Ring} to {AmiId}",
                ringName.Value, targetAmiId);
        }

        _logger.LogInformation(
            "Rollback complete: ring {Ring} repointed from {OldAmi} to {NewAmi} ({Reason})",
            ringName.Value, currentRingState.CurrentAmiId, targetAmiId, reason);

        _metrics.RollbackExecuted(ringName.Value, currentRingState.CurrentAmiId,
            targetAmiId, reason);

        return Result<RingState>.Success(newRingState);
    }
}
