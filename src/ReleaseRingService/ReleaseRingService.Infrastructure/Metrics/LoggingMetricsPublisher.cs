using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Metrics;

namespace ReleaseRingService.Infrastructure.Metrics;

public sealed class LoggingMetricsPublisher : IMetricsPublisher
{
    private readonly ILogger<LoggingMetricsPublisher> _logger;

    public LoggingMetricsPublisher(ILogger<LoggingMetricsPublisher> logger)
    {
        _logger = logger;
    }

    public void PromotionSucceeded(string ringName, string amiId)
    {
        _logger.LogInformation(
            "Metric: PromotionSucceeded Ring={RingName} AmiId={AmiId}",
            ringName, amiId);
    }

    public void PromotionFailed(string ringName, string amiId, string reason)
    {
        _logger.LogWarning(
            "Metric: PromotionFailed Ring={RingName} AmiId={AmiId} Reason={Reason}",
            ringName, amiId, reason);
    }

    public void RollbackExecuted(string ringName, string fromAmiId, string toAmiId, string reason)
    {
        _logger.LogInformation(
            "Metric: RollbackExecuted Ring={RingName} FromAmiId={FromAmiId} ToAmiId={ToAmiId} Reason={Reason}",
            ringName, fromAmiId, toAmiId, reason);
    }

    public void PromotionsPaused(string reason)
    {
        _logger.LogInformation(
            "Metric: PromotionsPaused Reason={Reason}", reason);
    }

    public void PromotionsResumed()
    {
        _logger.LogInformation("Metric: PromotionsResumed");
    }
}
