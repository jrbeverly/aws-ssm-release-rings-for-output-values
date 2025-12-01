using ReleaseRingService.Core.Errors;

namespace ReleaseRingService.Core.Commands;

public interface IPausePromotionsCommand
{
    Task<Result> ExecuteAsync(string reason, CancellationToken ct);
}
