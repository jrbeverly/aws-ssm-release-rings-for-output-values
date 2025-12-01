namespace ReleaseRingService.Infrastructure.Ec2;

public interface IEc2ImageValidator
{
    Task<bool> AmiExistsAsync(string amiId, CancellationToken ct);
    Task<bool> IsValidImageReferenceAsync(string amiId, CancellationToken ct);
}
