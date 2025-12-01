using ReleaseRingService.Core.Models;

namespace ReleaseRingService.Infrastructure.DynamoDb;

public interface IDynamoDbRepository
{
    Task<SystemConfig?> GetSystemConfigAsync(CancellationToken ct);
    Task SaveSystemConfigAsync(SystemConfig config, CancellationToken ct);
    Task<Candidate?> GetCandidateAsync(string amiId, CancellationToken ct);
    Task SaveCandidateAsync(Candidate candidate, CancellationToken ct);
    Task<IReadOnlyList<Candidate>> GetCandidatesByStatusAsync(
        CandidateLifecycleStatus status, CancellationToken ct);
    Task<RingState?> GetRingStateAsync(RingName ringName, CancellationToken ct);
    Task SaveRingStateAsync(RingState state, CancellationToken ct);
    Task<IReadOnlyList<RingState>> GetAllRingStatesAsync(CancellationToken ct);
    Task AppendTransitionAsync(TransitionRecord record, CancellationToken ct);
    Task<IReadOnlyList<TransitionRecord>> GetRecentTransitionsAsync(int limit, CancellationToken ct);
}
