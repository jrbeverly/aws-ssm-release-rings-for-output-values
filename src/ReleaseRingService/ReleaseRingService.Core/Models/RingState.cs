namespace ReleaseRingService.Core.Models;

public sealed record RingState
{
    public required RingName RingName { get; init; }
    public required string CurrentAmiId { get; init; }
    public string? PreviousAmiId { get; init; }
    public required DateTime CurrentPublishedAt { get; init; }
    public DateTime? PreviousPublishedAt { get; init; }
    public string? LastTransitionMetadata { get; init; }

    /// <summary>
    /// Monotonic concurrency token for conditional DynamoDB writes.
    /// Incremented by the repository on each ring update. Starts at 1.
    /// </summary>
    public long Version { get; init; } = 1;
}
