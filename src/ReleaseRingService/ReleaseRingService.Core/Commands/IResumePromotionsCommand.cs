using ReleaseRingService.Core.Errors;

namespace ReleaseRingService.Core.Commands;

public interface IResumePromotionsCommand
{
    Task<Result> ExecuteAsync(CancellationToken ct);
}
