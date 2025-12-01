using ReleaseRingService.Core.Errors;

namespace ReleaseRingService.Core.Commands;

public sealed record ReconcileResult
{
    public required IReadOnlyList<string> PromotedRings { get; init; }
    public required IReadOnlyList<string> SkippedRings { get; init; }
}

public interface IReconcileCommand
{
    Task<Result<ReconcileResult>> ExecuteAsync(CancellationToken ct);
}
