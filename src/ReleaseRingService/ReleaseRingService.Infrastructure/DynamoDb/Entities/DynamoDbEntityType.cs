namespace ReleaseRingService.Infrastructure.DynamoDb.Entities;

public enum DynamoDbEntityType
{
    SystemConfig = 1,
    Candidate = 2,
    RingState = 3,
    TransitionRecord = 4
}
