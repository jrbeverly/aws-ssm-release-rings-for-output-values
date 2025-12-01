using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace ReleaseRingService.Infrastructure.Ssm;

public sealed class AwsSsmParameterStore : ISsmParameterStore
{
    private readonly IAmazonSimpleSystemsManagement _ssm;

    public AwsSsmParameterStore(IAmazonSimpleSystemsManagement ssm)
    {
        _ssm = ssm;
    }

    public async Task<string?> GetParameterValueAsync(string parameterName, CancellationToken ct)
    {
        try
        {
            var response = await _ssm.GetParameterAsync(new GetParameterRequest
            {
                Name = parameterName,
                WithDecryption = false
            }, ct);

            return response.Parameter?.Value;
        }
        catch (ParameterNotFoundException)
        {
            return null;
        }
    }

    public async Task PublishParameterAsync(string parameterName, string amiId, CancellationToken ct)
    {
        await _ssm.PutParameterAsync(new PutParameterRequest
        {
            Name = parameterName,
            Value = amiId,
            Type = ParameterType.String,
            DataType = "aws:ec2:image",
            Overwrite = true
        }, ct);
    }
}
