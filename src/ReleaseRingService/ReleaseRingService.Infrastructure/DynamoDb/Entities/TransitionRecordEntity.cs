using System.Globalization;
using Amazon.DynamoDBv2.Model;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.DynamoDb.Keys;

namespace ReleaseRingService.Infrastructure.DynamoDb.Entities;

public sealed record TransitionRecordEntity
{
    public required string PK { get; init; }
    public required string SK { get; init; }
    public required string GSI1PK { get; init; }
    public required string GSI1SK { get; init; }
    public required DynamoDbEntityType EntityType { get; init; }

    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required TransitionType Type { get; init; }
    public string? RingName { get; init; }
    public string? AmiId { get; init; }
    public string? Metadata { get; init; }
    public string? OperatorReason { get; init; }

    public TransitionRecord ToDomain()
    {
        return new TransitionRecord
        {
            Id = Id,
            Timestamp = Timestamp,
            Type = Type,
            RingName = RingName is not null
                ? Core.Models.RingName.From(RingName)
                : null,
            AmiId = AmiId,
            Metadata = Metadata,
            OperatorReason = OperatorReason
        };
    }

    public static TransitionRecordEntity FromDomain(TransitionRecord record)
    {
        var ringNameStr = record.RingName?.Value ?? "__global__";

        return new TransitionRecordEntity
        {
            PK = DynamoKeyScheme.TransitionPk(ringNameStr),
            SK = DynamoKeyScheme.TransitionSk(record.Timestamp, record.Id),
            GSI1PK = DynamoKeyScheme.TransitionSk(record.Timestamp, record.Id),
            GSI1SK = DynamoKeyScheme.TransitionPk(ringNameStr),
            EntityType = DynamoDbEntityType.TransitionRecord,
            Id = record.Id,
            Timestamp = record.Timestamp,
            Type = record.Type,
            RingName = record.RingName?.Value,
            AmiId = record.AmiId,
            Metadata = record.Metadata,
            OperatorReason = record.OperatorReason
        };
    }

    internal static TransitionRecordEntity FromDynamoItem(Dictionary<string, AttributeValue> item)
    {
        return new TransitionRecordEntity
        {
            PK = item["PK"].S,
            SK = item["SK"].S,
            GSI1PK = item["GSI1PK"].S,
            GSI1SK = item["GSI1SK"].S,
            EntityType = (DynamoDbEntityType)int.Parse(item["EntityType"].N, CultureInfo.InvariantCulture),
            Id = item["Id"].S,
            Timestamp = DateTime.Parse(item["Timestamp"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Type = Enum.Parse<TransitionType>(item["Type"].S),
            RingName = item.TryGetValue("RingName", out var rn) && !rn.NULL ? rn.S : null,
            AmiId = item.TryGetValue("AmiId", out var ai) && !ai.NULL ? ai.S : null,
            Metadata = item.TryGetValue("Metadata", out var md) && !md.NULL ? md.S : null,
            OperatorReason = item.TryGetValue("OperatorReason", out var or) && !or.NULL ? or.S : null
        };
    }
}
