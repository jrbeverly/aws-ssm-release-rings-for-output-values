namespace ReleaseRingService.Core.Metrics;

public interface IMetricsPublisher
{
    void PromotionSucceeded(string ringName, string amiId);
    void PromotionFailed(string ringName, string amiId, string reason);
    void RollbackExecuted(string ringName, string fromAmiId, string toAmiId, string reason);
    void PromotionsPaused(string reason);
    void PromotionsResumed();
}
