namespace ReleaseRingService.Core.Models;

public enum TransitionType
{
    CandidateRegistered,
    RingPublication,
    Rollback,
    PromotionPaused,
    PromotionResumed,
    PublishFailed
}

public sealed record TransitionRecord
{
    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required TransitionType Type { get; init; }
    public RingName? RingName { get; init; }
    public string? AmiId { get; init; }
    public string? Metadata { get; init; }
    public string? OperatorReason { get; init; }
}
