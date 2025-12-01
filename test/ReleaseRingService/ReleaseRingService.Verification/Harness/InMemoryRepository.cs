using System.Collections.Concurrent;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.DynamoDb;

namespace ReleaseRingService.Verification.Harness;

/// <summary>
/// Thread-safe in-memory implementation of IDynamoDbRepository for use in
/// verification / integration tests. Exercises the real command implementations
/// without requiring a real DynamoDB table.
/// </summary>
public sealed class InMemoryRepository : IDynamoDbRepository
{
    private readonly ConcurrentDictionary<string, SystemConfig> _systemConfigs = new();
    private readonly ConcurrentDictionary<string, Candidate> _candidates = new();
    private readonly ConcurrentDictionary<string, RingState> _ringStates = new();
    private readonly ConcurrentBag<TransitionRecord> _transitions = new();

    // ── SystemConfig ──────────────────────────────────────────────────────

    public Task<SystemConfig?> GetSystemConfigAsync(CancellationToken ct)
    {
        _systemConfigs.TryGetValue("SYSTEM", out var config);
        return Task.FromResult(config);
    }

    public Task SaveSystemConfigAsync(SystemConfig config, CancellationToken ct)
    {
        _systemConfigs["SYSTEM"] = config;
        return Task.CompletedTask;
    }

    // ── Candidate ─────────────────────────────────────────────────────────

    public Task<Candidate?> GetCandidateAsync(string amiId, CancellationToken ct)
    {
        _candidates.TryGetValue(amiId, out var candidate);
        return Task.FromResult(candidate);
    }

    public Task SaveCandidateAsync(Candidate candidate, CancellationToken ct)
    {
        _candidates[candidate.AmiId] = candidate;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Candidate>> GetCandidatesByStatusAsync(
        CandidateLifecycleStatus status, CancellationToken ct)
    {
        var results = _candidates.Values
            .Where(c => c.LifecycleStatus == status)
            .OrderBy(c => c.RegistrationTimestamp)
            .ToList();
        return Task.FromResult<IReadOnlyList<Candidate>>(results);
    }

    // ── RingState ─────────────────────────────────────────────────────────

    public Task<RingState?> GetRingStateAsync(RingName ringName, CancellationToken ct)
    {
        _ringStates.TryGetValue(ringName.Value, out var state);
        return Task.FromResult(state);
    }

    public Task SaveRingStateAsync(RingState state, CancellationToken ct)
    {
        _ringStates[state.RingName.Value] = state;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RingState>> GetAllRingStatesAsync(CancellationToken ct)
    {
        var results = RingName.All
            .Select(r => _ringStates.TryGetValue(r.Value, out var s) ? s : null)
            .Where(s => s is not null)
            .Cast<RingState>()
            .ToList();
        return Task.FromResult<IReadOnlyList<RingState>>(results);
    }

    // ── TransitionRecord ──────────────────────────────────────────────────

    public Task AppendTransitionAsync(TransitionRecord record, CancellationToken ct)
    {
        _transitions.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransitionRecord>> GetRecentTransitionsAsync(
        int limit, CancellationToken ct)
    {
        var results = _transitions
            .OrderByDescending(t => t.Timestamp)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<TransitionRecord>>(results);
    }

    // ── Verification helpers (not on IDynamoDbRepository) ─────────────────

    /// <summary>
    /// Returns all transitions in insertion order for assertions.
    /// </summary>
    public IReadOnlyList<TransitionRecord> AllTransitions()
    {
        return _transitions
            .OrderBy(t => t.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Returns all candidates for inspection.
    /// </summary>
    public IReadOnlyList<Candidate> AllCandidates()
    {
        return _candidates.Values.ToList();
    }
}
