using System.Collections.Immutable;
using System.Globalization;
using Amazon.DynamoDBv2.Model;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.DynamoDb.Keys;

namespace ReleaseRingService.Infrastructure.DynamoDb.Entities;

public sealed record CandidateEntity
{
    public required string PK { get; init; }
    public required string SK { get; init; }
    public required string GSI1PK { get; init; }
    public required string GSI1SK { get; init; }
    public required DynamoDbEntityType EntityType { get; init; }
    public required long Version { get; init; }

    public required string AmiId { get; init; }
    public required DateTime RegistrationTimestamp { get; init; }
    public required string SourceMetadata { get; init; }
    public required CandidateLifecycleStatus LifecycleStatus { get; init; }
    public IReadOnlyDictionary<string, string> PublicationTimestamps { get; init; } =
        new Dictionary<string, string>();

    public Candidate ToDomain()
    {
        return new Candidate
        {
            AmiId = AmiId,
            RegistrationTimestamp = RegistrationTimestamp,
            SourceMetadata = SourceMetadata,
            LifecycleStatus = LifecycleStatus,
            PublicationTimestamps = PublicationTimestamps
                .Select(kvp => new KeyValuePair<RingName, DateTime>(
                    RingName.From(kvp.Key)!,
                    DateTime.Parse(kvp.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)))
                .ToImmutableDictionary()
        };
    }

    public static CandidateEntity FromDomain(Candidate candidate)
    {
        return new CandidateEntity
        {
            PK = DynamoKeyScheme.CandidatePk(candidate.AmiId),
            SK = DynamoKeyScheme.CandidateSk(candidate.AmiId),
            GSI1PK = DynamoKeyScheme.Gsi1StatusPk(candidate.LifecycleStatus),
            GSI1SK = DynamoKeyScheme.Gsi1StatusSk(candidate.RegistrationTimestamp),
            EntityType = DynamoDbEntityType.Candidate,
            Version = candidate.Version,
            AmiId = candidate.AmiId,
            RegistrationTimestamp = candidate.RegistrationTimestamp,
            SourceMetadata = candidate.SourceMetadata,
            LifecycleStatus = candidate.LifecycleStatus,
            PublicationTimestamps = candidate.PublicationTimestamps
                .ToDictionary(kvp => kvp.Key.Value, kvp => kvp.Value.ToString("O"))
        };
    }

    internal static CandidateEntity FromDynamoItem(Dictionary<string, AttributeValue> item)
    {
        return new CandidateEntity
        {
            PK = item["PK"].S,
            SK = item["SK"].S,
            GSI1PK = item["GSI1PK"].S,
            GSI1SK = item["GSI1SK"].S,
            EntityType = (DynamoDbEntityType)int.Parse(item["EntityType"].N, CultureInfo.InvariantCulture),
            Version = long.Parse(item["Version"].N, CultureInfo.InvariantCulture),
            AmiId = item["AmiId"].S,
            RegistrationTimestamp = DateTime.Parse(item["RegistrationTimestamp"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            SourceMetadata = item["SourceMetadata"].S,
            LifecycleStatus = Enum.Parse<CandidateLifecycleStatus>(item["LifecycleStatus"].S),
            PublicationTimestamps = item["PublicationTimestamps"].M
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.S)
        };
    }
}
