using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Models;

namespace ReleaseRingService.Core.Ssm;

public interface IRingPublisher
{
    Task<Result<PublishRingResult>> PublishToRingAsync(
        RingName ring, string amiId, CancellationToken ct);
}

public sealed record PublishRingResult
{
    public required string ParameterName { get; init; }
    public required RingName Ring { get; init; }
    public required string AmiId { get; init; }
    public required DateTime PublishedAt { get; init; }
    public required long ParameterVersion { get; init; }
}
