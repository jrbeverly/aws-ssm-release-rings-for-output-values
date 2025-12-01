using System.Globalization;
using Amazon.DynamoDBv2.Model;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.DynamoDb.Keys;

namespace ReleaseRingService.Infrastructure.DynamoDb.Entities;

public sealed record RingStateEntity
{
    public required string PK { get; init; }
    public required string SK { get; init; }
    public required DynamoDbEntityType EntityType { get; init; }
    public required long Version { get; init; }

    public required string RingName { get; init; }
    public required string CurrentAmiId { get; init; }
    public string? PreviousAmiId { get; init; }
    public required DateTime CurrentPublishedAt { get; init; }
    public DateTime? PreviousPublishedAt { get; init; }
    public string? LastTransitionMetadata { get; init; }

    public RingState ToDomain()
    {
        return new RingState
        {
            RingName = Core.Models.RingName.From(RingName)!,
            CurrentAmiId = CurrentAmiId,
            PreviousAmiId = PreviousAmiId,
            CurrentPublishedAt = CurrentPublishedAt,
            PreviousPublishedAt = PreviousPublishedAt,
            LastTransitionMetadata = LastTransitionMetadata
        };
    }

    public static RingStateEntity FromDomain(RingState state)
    {
        return new RingStateEntity
        {
            PK = DynamoKeyScheme.RingStatePk(state.RingName.Value),
            SK = DynamoKeyScheme.RingStateSk(state.RingName.Value),
            EntityType = DynamoDbEntityType.RingState,
            Version = state.Version,
            RingName = state.RingName.Value,
            CurrentAmiId = state.CurrentAmiId,
            PreviousAmiId = state.PreviousAmiId,
            CurrentPublishedAt = state.CurrentPublishedAt,
            PreviousPublishedAt = state.PreviousPublishedAt,
            LastTransitionMetadata = state.LastTransitionMetadata
        };
    }

    internal static RingStateEntity FromDynamoItem(Dictionary<string, AttributeValue> item)
    {
        return new RingStateEntity
        {
            PK = item["PK"].S,
            SK = item["SK"].S,
            EntityType = (DynamoDbEntityType)int.Parse(item["EntityType"].N, CultureInfo.InvariantCulture),
            Version = long.Parse(item["Version"].N, CultureInfo.InvariantCulture),
            RingName = item["RingName"].S,
            CurrentAmiId = item["CurrentAmiId"].S,
            PreviousAmiId = item.TryGetValue("PreviousAmiId", out var prev) && !prev.NULL ? prev.S : null,
            CurrentPublishedAt = DateTime.Parse(item["CurrentPublishedAt"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            PreviousPublishedAt = item.TryGetValue("PreviousPublishedAt", out var prevAt) && !prevAt.NULL
                ? DateTime.Parse(prevAt.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null,
            LastTransitionMetadata = item.TryGetValue("LastTransitionMetadata", out var meta) && !meta.NULL ? meta.S : null
        };
    }
}
