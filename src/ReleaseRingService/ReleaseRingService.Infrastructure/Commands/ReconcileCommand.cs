using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Metrics;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Core.Ssm;
using ReleaseRingService.Infrastructure.DynamoDb;

namespace ReleaseRingService.Infrastructure.Commands;

public sealed class ReconcileCommand : IReconcileCommand
{
    private readonly IDynamoDbRepository _repository;
    private readonly IRingPublisher _ringPublisher;
    private readonly IMetricsPublisher _metrics;
    private readonly ILogger<ReconcileCommand> _logger;

    public ReconcileCommand(
        IDynamoDbRepository repository,
        IRingPublisher ringPublisher,
        IMetricsPublisher metrics,
        ILogger<ReconcileCommand> logger)
    {
        _repository = repository;
        _ringPublisher = ringPublisher;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<Result<ReconcileResult>> ExecuteAsync(CancellationToken ct)
    {
        // 1. Load system configuration.
        var config = await _repository.GetSystemConfigAsync(ct);
        if (config is null)
        {
            return Result<ReconcileResult>.Failure(new Error(
                "NO_CONFIGURATION",
                "System configuration has not been initialised. The control-plane table must be bootstrapped before reconciliation can run."));
        }

        // 2. If promotions are paused, exit without mutating anything.
        if (config.IsPromotionPaused)
        {
            _logger.LogInformation(
                "Promotions are paused (reason: {Reason}). Reconciliation exiting without changes.",
                config.PauseReason ?? "(none)");

            return Result<ReconcileResult>.Success(new ReconcileResult
            {
                PromotedRings = [],
                SkippedRings = config.Rings.Skip(1).Select(r => r.Value).ToList()
            });
        }

        // 3. Build ordered ring transitions from the configured ring list and soak durations.
        var transitionLookup = RingTransition
            .BuildFromRingOrder(config.Rings, config.SoakDurations)
            .ToDictionary(t => t.To);

        // 4. Load active candidates in registration order (oldest first via GSI1).
        var candidates = await _repository.GetCandidatesByStatusAsync(
            CandidateLifecycleStatus.Active, ct);

        // 5. Load current ring state for all rings.
        var ringStates = await _repository.GetAllRingStatesAsync(ct);

        var now = DateTime.UtcNow;
        var promoted = new List<string>();
        var skipped = new List<string>();

        // 6. For each target ring after the first (nightly), attempt one promotion.
        for (int i = 1; i < config.Rings.Length; i++)
        {
            var toRing = config.Rings[i];

            if (!transitionLookup.TryGetValue(toRing, out var transition))
            {
                _logger.LogWarning("No soak duration configured for transition into {Ring}; skipping", toRing.Value);
                skipped.Add(toRing.Value);
                continue;
            }

            var fromRing = transition.From;

            // Find the oldest active candidate that:
            //   - Is published to the previous ring (fromRing)
            //   - Has satisfied the soak duration for that transition
            //   - Is NOT yet published to this ring (toRing)
            Candidate? eligible = null;
            foreach (var candidate in candidates)
            {
                if (!candidate.PublicationTimestamps.TryGetValue(fromRing, out var publishedAt))
                    continue;

                if (candidate.PublicationTimestamps.ContainsKey(toRing))
                    continue;

                if (now - publishedAt < transition.SoakDuration)
                    continue;

                eligible = candidate;
                break;
            }

            if (eligible is null)
            {
                _logger.LogDebug(
                    "No eligible candidate for ring {Ring} (transition {From}->{To}, soak {Soak})",
                    toRing, fromRing.Value, toRing.Value, transition.SoakDuration);
                skipped.Add(toRing.Value);
                continue;
            }

            // 7. Publish to SSM.
            var publishResult = await _ringPublisher.PublishToRingAsync(toRing, eligible.AmiId, ct);

            if (publishResult.IsFailure)
            {
                _logger.LogError(
                    "SSM publish failed for candidate {AmiId} to ring {Ring}: {Error}",
                    eligible.AmiId, toRing, publishResult.Error);

                await _repository.AppendTransitionAsync(new TransitionRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = now,
                    Type = TransitionType.PublishFailed,
                    RingName = toRing,
                    AmiId = eligible.AmiId,
                    Metadata = publishResult.Error?.ToString()
                }, ct);

                _metrics.PromotionFailed(toRing.Value, eligible.AmiId,
                    publishResult.Error?.ToString() ?? "unknown");

                skipped.Add(toRing.Value);
                continue;
            }

            // 8. Update ring state — move current to previous, set new AMI as current.
            var currentRingState = ringStates.FirstOrDefault(rs => rs.RingName == toRing);
            var newRingState = currentRingState is not null
                ? currentRingState with
                {
                    PreviousAmiId = currentRingState.CurrentAmiId,
                    PreviousPublishedAt = currentRingState.CurrentPublishedAt,
                    CurrentAmiId = eligible.AmiId,
                    CurrentPublishedAt = now,
                    LastTransitionMetadata = $"auto-promotion from {fromRing.Value}",
                    Version = currentRingState.Version + 1
                }
                : new RingState
                {
                    RingName = toRing,
                    CurrentAmiId = eligible.AmiId,
                    CurrentPublishedAt = now,
                    LastTransitionMetadata = $"auto-promotion from {fromRing.Value}"
                };

            await _repository.SaveRingStateAsync(newRingState, ct);

            // 9. Record publication on the candidate.
            var updatedCandidate = eligible with
            {
                PublicationTimestamps = eligible.PublicationTimestamps
                    .SetItem(toRing, now)
            };
            await _repository.SaveCandidateAsync(updatedCandidate, ct);

            // 10. Record transition history.
            await _repository.AppendTransitionAsync(new TransitionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = now,
                Type = TransitionType.RingPublication,
                RingName = toRing,
                AmiId = eligible.AmiId,
                Metadata = $"auto-promotion from {fromRing.Value}"
            }, ct);

            _logger.LogInformation(
                "Promoted candidate {AmiId} to ring {Ring} ({From}->{To}, soaked {Soak})",
                eligible.AmiId, toRing, fromRing.Value, toRing.Value, transition.SoakDuration);

            _metrics.PromotionSucceeded(toRing.Value, eligible.AmiId);

            promoted.Add(toRing.Value);

            // Update in-memory candidate list so this candidate is not still
            // considered "unpublished" for this ring by a later ring check.
            candidates = candidates
                .Select(c => c.AmiId == eligible.AmiId ? updatedCandidate : c)
                .ToList();
        }

        return Result<ReconcileResult>.Success(new ReconcileResult
        {
            PromotedRings = promoted,
            SkippedRings = skipped
        });
    }
}
