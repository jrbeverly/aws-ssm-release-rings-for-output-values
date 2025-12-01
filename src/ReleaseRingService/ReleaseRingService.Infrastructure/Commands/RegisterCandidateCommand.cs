using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Metrics;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Core.Ssm;
using ReleaseRingService.Infrastructure.DynamoDb;
using ReleaseRingService.Infrastructure.Ec2;

namespace ReleaseRingService.Infrastructure.Commands;

public sealed class RegisterCandidateCommand : IRegisterCandidateCommand
{
    private readonly IDynamoDbRepository _repository;
    private readonly IRingPublisher _ringPublisher;
    private readonly IEc2ImageValidator _ec2Validator;
    private readonly IMetricsPublisher _metrics;
    private readonly ILogger<RegisterCandidateCommand> _logger;

    public RegisterCandidateCommand(
        IDynamoDbRepository repository,
        IRingPublisher ringPublisher,
        IEc2ImageValidator ec2Validator,
        IMetricsPublisher metrics,
        ILogger<RegisterCandidateCommand> logger)
    {
        _repository = repository;
        _ringPublisher = ringPublisher;
        _ec2Validator = ec2Validator;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<Result<RegisterCandidateResult>> ExecuteAsync(
        string amiId, string? sourceMetadata, CancellationToken ct)
    {
        // Validate that the AMI exists in the configured account and region.
        var exists = await _ec2Validator.AmiExistsAsync(amiId, ct);
        if (!exists)
        {
            _logger.LogWarning("Candidate AMI {AmiId} not found in account/region", amiId);
            return Result<RegisterCandidateResult>.Failure(new Error(
                "AMI_NOT_FOUND",
                $"AMI '{amiId}' was not found in the configured account and region."));
        }

        var now = DateTime.UtcNow;

        // Idempotency — check whether this AMI is already a known candidate.
        var existing = await _repository.GetCandidateAsync(amiId, ct);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Candidate {AmiId} already registered at {Timestamp} (status={Status})",
                amiId, existing.RegistrationTimestamp, existing.LifecycleStatus);

            // Reactivate a withdrawn or superseded candidate so it re-enters the stream.
            if (existing.LifecycleStatus != CandidateLifecycleStatus.Active)
            {
                _logger.LogInformation(
                    "Reactivating candidate {AmiId} from {OldStatus} to Active",
                    amiId, existing.LifecycleStatus);

                var reactivated = existing with
                {
                    LifecycleStatus = CandidateLifecycleStatus.Active
                };
                await _repository.SaveCandidateAsync(reactivated, ct);

                return await PublishToNightlyAndRecordAsync(
                    reactivated, sourceMetadata, now, ct);
            }

            // If an active candidate has no nightly publication (partial failure
            // recovery), re-attempt the publish instead of returning AlreadyKnown.
            if (!existing.PublicationTimestamps.ContainsKey(RingName.Nightly))
            {
                _logger.LogInformation(
                    "Candidate {AmiId} active but not published to nightly — re-attempting publish",
                    amiId);
                return await PublishToNightlyAndRecordAsync(
                    existing, sourceMetadata, now, ct);
            }

            return Result<RegisterCandidateResult>.Success(new RegisterCandidateResult
            {
                Outcome = RegistrationOutcome.AlreadyKnown,
                Candidate = existing
            });
        }

        // Create the candidate record, then publish to nightly.
        var candidate = new Candidate
        {
            AmiId = amiId,
            RegistrationTimestamp = now,
            SourceMetadata = sourceMetadata ?? string.Empty,
            LifecycleStatus = CandidateLifecycleStatus.Active
        };

        await _repository.SaveCandidateAsync(candidate, ct);

        return await PublishToNightlyAndRecordAsync(
            candidate, sourceMetadata, now, ct);
    }

    private async Task<Result<RegisterCandidateResult>> PublishToNightlyAndRecordAsync(
        Candidate candidate, string? sourceMetadata, DateTime now, CancellationToken ct)
    {
        var amiId = candidate.AmiId;

        // Publish to the Nightly ring.
        var publishResult = await _ringPublisher.PublishToRingAsync(
            RingName.Nightly, amiId, ct);

        if (publishResult.IsFailure)
        {
            _logger.LogError(
                "Failed to publish candidate {AmiId} to nightly ring: {Error}",
                amiId, publishResult.Error);

            // Record the failure so operators can inspect what happened.
            await _repository.AppendTransitionAsync(new TransitionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = now,
                Type = TransitionType.PublishFailed,
                RingName = RingName.Nightly,
                AmiId = amiId,
                Metadata = publishResult.Error?.ToString()
            }, ct);

            _metrics.PromotionFailed(RingName.Nightly.Value, amiId,
                publishResult.Error?.ToString() ?? "unknown");

            return Result<RegisterCandidateResult>.Failure(publishResult.Error!);
        }

        // Update ring state for the Nightly ring.
        var previousState = await _repository.GetRingStateAsync(RingName.Nightly, ct);
        var newRingState = previousState is not null
            ? previousState with
            {
                PreviousAmiId = previousState.CurrentAmiId,
                PreviousPublishedAt = previousState.CurrentPublishedAt,
                CurrentAmiId = amiId,
                CurrentPublishedAt = now,
                LastTransitionMetadata = "registered-candidate",
                Version = previousState.Version + 1
            }
            : new RingState
            {
                RingName = RingName.Nightly,
                CurrentAmiId = amiId,
                CurrentPublishedAt = now,
                LastTransitionMetadata = "registered-candidate"
            };

        await _repository.SaveRingStateAsync(newRingState, ct);

        // Record the nightly publication on the candidate record.
        var updated = candidate with
        {
            PublicationTimestamps = candidate.PublicationTimestamps
                .SetItem(RingName.Nightly, now)
        };
        await _repository.SaveCandidateAsync(updated, ct);

        // Record transition history. If this candidate has no prior publications
        // (brand-new or recovery from a failed first attempt), record the registration.
        if (candidate.PublicationTimestamps.IsEmpty)
        {
            await _repository.AppendTransitionAsync(new TransitionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = now,
                Type = TransitionType.CandidateRegistered,
                AmiId = amiId,
                Metadata = sourceMetadata
            }, ct);
        }

        await _repository.AppendTransitionAsync(new TransitionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = now,
            Type = TransitionType.RingPublication,
            RingName = RingName.Nightly,
            AmiId = amiId,
            Metadata = "registered-candidate"
        }, ct);

        _metrics.PromotionSucceeded(RingName.Nightly.Value, amiId);

        _logger.LogInformation(
            "Candidate {AmiId} registered and published to nightly at {Timestamp}",
            amiId, now);

        return Result<RegisterCandidateResult>.Success(new RegisterCandidateResult
        {
            Outcome = RegistrationOutcome.Created,
            Candidate = updated
        });
    }
}
