namespace ReleaseRingService.Infrastructure.Ssm;

public interface ISsmParameterStore
{
    Task<string?> GetParameterValueAsync(string parameterName, CancellationToken ct);
    Task PublishParameterAsync(string parameterName, string amiId, CancellationToken ct);
}
