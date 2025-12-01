using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Models;

namespace ReleaseRingService.Core.Commands;

public interface IRollbackRingCommand
{
    Task<Result<RingState>> ExecuteAsync(
        RingName ringName, string targetAmiId, string reason, CancellationToken ct);
}
