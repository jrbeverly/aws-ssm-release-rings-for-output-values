using System.Collections.Immutable;

namespace ReleaseRingService.Core.Models;

public enum CandidateLifecycleStatus
{
    Active,
    Superseded,
    Withdrawn
}

public sealed record Candidate
{
    public required string AmiId { get; init; }
    public required DateTime RegistrationTimestamp { get; init; }
    public required string SourceMetadata { get; init; }
    public required CandidateLifecycleStatus LifecycleStatus { get; init; }
    public ImmutableDictionary<RingName, DateTime> PublicationTimestamps { get; init; } =
        ImmutableDictionary<RingName, DateTime>.Empty;

    /// <summary>
    /// Monotonic concurrency token for conditional DynamoDB writes.
    /// Incremented by the repository on each mutation. Starts at 1.
    /// </summary>
    public long Version { get; init; } = 1;
}
