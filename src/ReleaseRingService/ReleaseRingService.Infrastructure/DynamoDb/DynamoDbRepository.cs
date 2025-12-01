using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Infrastructure.DynamoDb.Entities;
using ReleaseRingService.Infrastructure.DynamoDb.Keys;

namespace ReleaseRingService.Infrastructure.DynamoDb;

public sealed class DynamoDbRepository : IDynamoDbRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    private const string Gsi1 = "GSI1";

    public DynamoDbRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
    }

    // ── SystemConfig ──────────────────────────────────────────────────────

    public async Task<SystemConfig?> GetSystemConfigAsync(CancellationToken ct)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = AsS(DynamoKeyScheme.SystemConfigPk),
                ["SK"] = AsS(DynamoKeyScheme.SystemConfigSk)
            }
        }, ct);

        return response.Item.Count > 0 ? MapSystemConfig(response.Item) : null;
    }

    public async Task SaveSystemConfigAsync(SystemConfig config, CancellationToken ct)
    {
        var entity = SystemConfigEntity.FromDomain(config);
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = MapSystemConfigEntity(entity)
        }, ct);
    }

    // ── Candidate ─────────────────────────────────────────────────────────

    public async Task<Candidate?> GetCandidateAsync(string amiId, CancellationToken ct)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = AsS(DynamoKeyScheme.CandidatePk(amiId)),
                ["SK"] = AsS(DynamoKeyScheme.CandidateSk(amiId))
            }
        }, ct);

        return response.Item.Count > 0
            ? CandidateEntity.FromDynamoItem(response.Item).ToDomain()
            : null;
    }

    public async Task SaveCandidateAsync(Candidate candidate, CancellationToken ct)
    {
        var entity = CandidateEntity.FromDomain(candidate);
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = MapCandidateEntity(entity)
        }, ct);
    }

    public async Task<IReadOnlyList<Candidate>> GetCandidatesByStatusAsync(
        CandidateLifecycleStatus status, CancellationToken ct)
    {
        var results = new List<Candidate>();
        string? exclusiveStartKey = null;

        do
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = Gsi1,
                KeyConditionExpression = "GSI1PK = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = AsS(DynamoKeyScheme.Gsi1StatusPk(status))
                }
            };

            if (exclusiveStartKey is not null)
                request.ExclusiveStartKey = new Dictionary<string, AttributeValue>
                {
                    ["GSI1PK"] = AsS(DynamoKeyScheme.Gsi1StatusPk(status)),
                    ["GSI1SK"] = AsS(exclusiveStartKey)
                };

            var response = await _dynamoDb.QueryAsync(request, ct);

            foreach (var item in response.Items)
                results.Add(CandidateEntity.FromDynamoItem(item).ToDomain());

            // The last evaluated key is from the index; extract the GSI1SK value.
            exclusiveStartKey = response.LastEvaluatedKey?.TryGetValue("GSI1SK", out var sk) == true
                ? sk.S
                : null;

        } while (exclusiveStartKey is not null);

        return results;
    }

    // ── RingState ─────────────────────────────────────────────────────────

    public async Task<RingState?> GetRingStateAsync(RingName ringName, CancellationToken ct)
    {
        var name = ringName.Value;
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = AsS(DynamoKeyScheme.RingStatePk(name)),
                ["SK"] = AsS(DynamoKeyScheme.RingStateSk(name))
            }
        }, ct);

        return response.Item.Count > 0
            ? RingStateEntity.FromDynamoItem(response.Item).ToDomain()
            : null;
    }

    public async Task SaveRingStateAsync(RingState state, CancellationToken ct)
    {
        var entity = RingStateEntity.FromDomain(state);
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = MapRingStateEntity(entity)
        }, ct);
    }

    public async Task<IReadOnlyList<RingState>> GetAllRingStatesAsync(CancellationToken ct)
    {
        var keys = RingName.All
            .Select(r => new Dictionary<string, AttributeValue>
            {
                ["PK"] = AsS(DynamoKeyScheme.RingStatePk(r.Value)),
                ["SK"] = AsS(DynamoKeyScheme.RingStateSk(r.Value))
            })
            .ToList();

        var response = await _dynamoDb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>
            {
                [_tableName] = new KeysAndAttributes { Keys = keys }
            }
        }, ct);

        var items = response.Responses.TryGetValue(_tableName, out var tableItems)
            ? tableItems
            : [];

        return items
            .Select(item => RingStateEntity.FromDynamoItem(item).ToDomain())
            .ToList();
    }

    // ── TransitionRecord ──────────────────────────────────────────────────

    public async Task AppendTransitionAsync(TransitionRecord record, CancellationToken ct)
    {
        var entity = TransitionRecordEntity.FromDomain(record);
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = MapTransitionRecordEntity(entity)
        }, ct);
    }

    public async Task<IReadOnlyList<TransitionRecord>> GetRecentTransitionsAsync(
        int limit, CancellationToken ct)
    {
        // Collect transitions from all ring partitions plus global.
        var ringPartitions = RingName.All.Select(r => $"HISTORY#{r.Value}").ToList();
        ringPartitions.Add("HISTORY#__global__");

        var allRecords = new List<TransitionRecord>();

        foreach (var pk in ringPartitions)
        {
            if (allRecords.Count >= limit) break;

            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "PK = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = AsS(pk)
                },
                ScanIndexForward = false,
                Limit = limit
            }, ct);

            foreach (var item in response.Items)
                allRecords.Add(TransitionRecordEntity.FromDynamoItem(item).ToDomain());
        }

        return allRecords
            .OrderByDescending(r => r.Timestamp)
            .Take(limit)
            .ToList();
    }

    // ── Attribute helpers ─────────────────────────────────────────────────

    private static AttributeValue AsS(string value) => new() { S = value };

    private static AttributeValue AsN(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static AttributeValue AsBool(bool value) => new() { BOOL = value };

    private static AttributeValue AsNullableS(string? value) =>
        value is not null ? AsS(value) : new AttributeValue { NULL = true };

    private static AttributeValue AsStringList(IEnumerable<string> values) =>
        new() { SS = values.ToList() };

    private static AttributeValue AsStringMap(IReadOnlyDictionary<string, string> map) =>
        new() { M = map.ToDictionary(kvp => kvp.Key, kvp => AsS(kvp.Value)), IsMSet = true };

    // ── SystemConfig mapping ──────────────────────────────────────────────

    private static SystemConfig MapSystemConfig(Dictionary<string, AttributeValue> item)
    {
        var entity = new SystemConfigEntity
        {
            PK = item["PK"].S,
            SK = item["SK"].S,
            EntityType = (DynamoDbEntityType)int.Parse(item["EntityType"].N, CultureInfo.InvariantCulture),
            Version = long.Parse(item["Version"].N, CultureInfo.InvariantCulture),
            Region = item["Region"].S,
            ParameterPrefix = item["ParameterPrefix"].S,
            Rings = [.. item["Rings"].SS],
            SoakDurations = item["SoakDurations"].M
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.S),
            IsPromotionPaused = item["IsPromotionPaused"].BOOL,
            PauseReason = item.TryGetValue("PauseReason", out var pr) && !pr.NULL ? pr.S : null
        };
        return entity.ToDomain();
    }

    private static Dictionary<string, AttributeValue> MapSystemConfigEntity(SystemConfigEntity entity)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = AsS(entity.PK),
            ["SK"] = AsS(entity.SK),
            ["EntityType"] = AsN((int)entity.EntityType),
            ["Version"] = AsN(entity.Version),
            ["Region"] = AsS(entity.Region),
            ["ParameterPrefix"] = AsS(entity.ParameterPrefix),
            ["Rings"] = AsStringList(entity.Rings),
            ["SoakDurations"] = AsStringMap(entity.SoakDurations),
            ["IsPromotionPaused"] = AsBool(entity.IsPromotionPaused)
        };

        if (entity.PauseReason is not null)
            item["PauseReason"] = AsS(entity.PauseReason);

        return item;
    }

    // ── Candidate mapping ─────────────────────────────────────────────────

    internal static Dictionary<string, AttributeValue> MapCandidateEntity(CandidateEntity entity)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = AsS(entity.PK),
            ["SK"] = AsS(entity.SK),
            ["GSI1PK"] = AsS(entity.GSI1PK),
            ["GSI1SK"] = AsS(entity.GSI1SK),
            ["EntityType"] = AsN((int)entity.EntityType),
            ["Version"] = AsN(entity.Version),
            ["AmiId"] = AsS(entity.AmiId),
            ["RegistrationTimestamp"] = AsS(entity.RegistrationTimestamp.ToString("O")),
            ["SourceMetadata"] = AsS(entity.SourceMetadata),
            ["LifecycleStatus"] = AsS(entity.LifecycleStatus.ToString()),
            ["PublicationTimestamps"] = AsStringMap(entity.PublicationTimestamps
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
        };
        return item;
    }

    // ── RingState mapping ─────────────────────────────────────────────────

    private static Dictionary<string, AttributeValue> MapRingStateEntity(RingStateEntity entity)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = AsS(entity.PK),
            ["SK"] = AsS(entity.SK),
            ["EntityType"] = AsN((int)entity.EntityType),
            ["Version"] = AsN(entity.Version),
            ["RingName"] = AsS(entity.RingName),
            ["CurrentAmiId"] = AsS(entity.CurrentAmiId),
            ["CurrentPublishedAt"] = AsS(entity.CurrentPublishedAt.ToString("O"))
        };

        if (entity.PreviousAmiId is not null)
            item["PreviousAmiId"] = AsS(entity.PreviousAmiId);
        if (entity.PreviousPublishedAt is not null)
            item["PreviousPublishedAt"] = AsS(entity.PreviousPublishedAt.Value.ToString("O"));
        if (entity.LastTransitionMetadata is not null)
            item["LastTransitionMetadata"] = AsS(entity.LastTransitionMetadata);

        return item;
    }

    // ── TransitionRecord mapping ──────────────────────────────────────────

    private static Dictionary<string, AttributeValue> MapTransitionRecordEntity(TransitionRecordEntity entity)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = AsS(entity.PK),
            ["SK"] = AsS(entity.SK),
            ["GSI1PK"] = AsS(entity.GSI1PK),
            ["GSI1SK"] = AsS(entity.GSI1SK),
            ["EntityType"] = AsN((int)entity.EntityType),
            ["Id"] = AsS(entity.Id),
            ["Timestamp"] = AsS(entity.Timestamp.ToString("O")),
            ["Type"] = AsS(entity.Type.ToString())
        };

        if (entity.RingName is not null)
            item["RingName"] = AsS(entity.RingName);
        if (entity.AmiId is not null)
            item["AmiId"] = AsS(entity.AmiId);
        if (entity.Metadata is not null)
            item["Metadata"] = AsS(entity.Metadata);
        if (entity.OperatorReason is not null)
            item["OperatorReason"] = AsS(entity.OperatorReason);

        return item;
    }
}
