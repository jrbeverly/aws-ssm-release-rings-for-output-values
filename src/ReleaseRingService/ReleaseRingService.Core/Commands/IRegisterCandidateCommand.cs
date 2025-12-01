using ReleaseRingService.Core.Errors;

namespace ReleaseRingService.Core.Commands;

public interface IRegisterCandidateCommand
{
    Task<Result<RegisterCandidateResult>> ExecuteAsync(
        string amiId, string? sourceMetadata, CancellationToken ct);
}
