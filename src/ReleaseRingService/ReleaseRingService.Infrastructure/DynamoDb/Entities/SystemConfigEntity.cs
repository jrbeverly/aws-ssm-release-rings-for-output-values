using System.Collections.Immutable;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.DynamoDb.Keys;

namespace ReleaseRingService.Infrastructure.DynamoDb.Entities;

public sealed record SystemConfigEntity
{
    public required string PK { get; init; }
    public required string SK { get; init; }
    public required DynamoDbEntityType EntityType { get; init; }
    public required long Version { get; init; }

    public required string Region { get; init; }
    public required string ParameterPrefix { get; init; }
    public required ImmutableArray<string> Rings { get; init; }
    public required IReadOnlyDictionary<string, string> SoakDurations { get; init; }
    public required bool IsPromotionPaused { get; init; }
    public string? PauseReason { get; init; }

    public SystemConfig ToDomain()
    {
        return new SystemConfig
        {
            Region = Region,
            ParameterPrefix = ParameterPrefix,
            // Rings is persisted as a DynamoDB string set, which does not keep order.
            Rings = Rings.Select(r => RingName.From(r)!)
                         .OrderBy(r => RingName.All.IndexOf(r))
                         .ToImmutableArray(),
            SoakDurations = SoakDurations.ToImmutableDictionary(
                kvp => kvp.Key,
                kvp => TimeSpan.Parse(kvp.Value)),
            IsPromotionPaused = IsPromotionPaused,
            PauseReason = PauseReason
        };
    }

    public static SystemConfigEntity FromDomain(SystemConfig config)
    {
        return new SystemConfigEntity
        {
            PK = DynamoKeyScheme.SystemConfigPk,
            SK = DynamoKeyScheme.SystemConfigSk,
            EntityType = DynamoDbEntityType.SystemConfig,
            Version = config.Version,
            Region = config.Region,
            ParameterPrefix = config.ParameterPrefix,
            Rings = config.Rings.Select(r => r.Value).ToImmutableArray(),
            SoakDurations = config.SoakDurations.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToString()),
            IsPromotionPaused = config.IsPromotionPaused,
            PauseReason = config.PauseReason
        };
    }
}
