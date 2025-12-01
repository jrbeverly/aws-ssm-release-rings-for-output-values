using System.Text.RegularExpressions;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Core.Ssm;

namespace ReleaseRingService.Infrastructure.Ssm;

public sealed partial class ManagedRingPublisher : IRingPublisher
{
    private readonly IAmazonSimpleSystemsManagement _ssm;
    private readonly string _parameterPrefix;
    private readonly ILogger<ManagedRingPublisher> _logger;

    public ManagedRingPublisher(
        IAmazonSimpleSystemsManagement ssm,
        string parameterPrefix,
        ILogger<ManagedRingPublisher> logger)
    {
        _ssm = ssm;
        _parameterPrefix = parameterPrefix.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(_parameterPrefix))
            throw new ArgumentException(
                "Parameter prefix must not be empty. A non-empty prefix is required to enforce the managed namespace.",
                nameof(parameterPrefix));

        _logger = logger;
    }

    public async Task<Result<PublishRingResult>> PublishToRingAsync(
        RingName ring, string amiId, CancellationToken ct)
    {
        if (!IsCompatibleWithEc2ImageType(amiId))
        {
            return Result<PublishRingResult>.Failure(new Error(
                "INVALID_AMI_ID",
                $"'{amiId}' is not a valid EC2 image identifier. AMI IDs must match the pattern ami-[0-9a-f]{{8,17}}."));
        }

        var parameterName = $"{_parameterPrefix}/{ring.Value}";

        if (!IsWithinManagedNamespace(parameterName))
        {
            return Result<PublishRingResult>.Failure(new Error(
                "NAMESPACE_VIOLATION",
                $"Parameter '{parameterName}' is outside the managed namespace '{_parameterPrefix}/'."));
        }

        try
        {
            var publishedAt = DateTime.UtcNow;

            var response = await _ssm.PutParameterAsync(new PutParameterRequest
            {
                Name = parameterName,
                Value = amiId,
                Type = ParameterType.String,
                DataType = "aws:ec2:image",
                Overwrite = true
            }, ct);

            _logger.LogInformation(
                "Published ring {Ring} parameter {ParameterName} to {AmiId} (version {Version})",
                ring.Value, parameterName, amiId, response.Version);

            return Result<PublishRingResult>.Success(new PublishRingResult
            {
                ParameterName = parameterName,
                Ring = ring,
                AmiId = amiId,
                PublishedAt = publishedAt,
                ParameterVersion = response.Version
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "SSM PutParameter failed for ring {Ring} parameter {ParameterName}",
                ring.Value, parameterName);

            return Result<PublishRingResult>.Failure(new Error(
                "SSM_PUBLISH_FAILED",
                $"SSM PutParameter failed for '{parameterName}': {ex.Message}"));
        }
    }

    private bool IsWithinManagedNamespace(string parameterName)
    {
        return parameterName.StartsWith(_parameterPrefix + "/", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^ami-[0-9a-f]{8,17}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmiIdPattern();

    private static bool IsCompatibleWithEc2ImageType(string amiId)
    {
        return !string.IsNullOrWhiteSpace(amiId) && AmiIdPattern().IsMatch(amiId);
    }
}
