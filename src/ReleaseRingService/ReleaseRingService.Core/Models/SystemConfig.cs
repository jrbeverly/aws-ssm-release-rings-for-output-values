using System.Collections.Immutable;

namespace ReleaseRingService.Core.Models;

public sealed record SystemConfig
{
    public required string Region { get; init; }
    public required string ParameterPrefix { get; init; }
    public required ImmutableArray<RingName> Rings { get; init; }
    public required ImmutableDictionary<string, TimeSpan> SoakDurations { get; init; }
    public required bool IsPromotionPaused { get; init; }
    public string? PauseReason { get; init; }

    /// <summary>
    /// Monotonic concurrency token for conditional DynamoDB writes.
    /// Incremented by the repository on each mutation. Starts at 1.
    /// </summary>
    public long Version { get; init; } = 1;

    public TimeSpan GetSoakDuration(RingName from, RingName to)
    {
        var key = $"{from.Value}->{to.Value}";
        return SoakDurations.TryGetValue(key, out var duration)
            ? duration
            : TimeSpan.Zero;
    }
}
