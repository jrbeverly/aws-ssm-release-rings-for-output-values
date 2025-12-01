using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Models;

namespace ReleaseRingService.Core.Commands;

public sealed record ShowStateResult
{
    public required IReadOnlyList<RingState> RingStates { get; init; }
    public required bool IsPromotionPaused { get; init; }
    public string? PauseReason { get; init; }
    public required IReadOnlyList<TransitionRecord> RecentTransitions { get; init; }
}

public interface IShowStateCommand
{
    Task<Result<ShowStateResult>> ExecuteAsync(CancellationToken ct);
}
