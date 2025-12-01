using System.Collections.Concurrent;
using System.Collections.Immutable;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Logging;
using Moq;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Metrics;
using ReleaseRingService.Core.Models;
using ReleaseRingService.Core.Ssm;
using ReleaseRingService.Infrastructure.Commands;
using ReleaseRingService.Infrastructure.Ec2;
using ReleaseRingService.Infrastructure.Metrics;
using ReleaseRingService.Infrastructure.Ssm;

namespace ReleaseRingService.Verification.Harness;

/// <summary>
/// Verification harness that wires real command implementations with an
/// in-memory repository and a configurable SSM publisher. Used to exercise
/// cross-command workflows and end-to-end release lifecycle scenarios.
///
/// Future quality-gate or validation work can extend this harness with
/// additional mock surfaces (e.g. EC2 validation) or behaviour-injection
/// controls.
/// </summary>
public sealed class VerificationHarness : IDisposable
{
    private readonly Mock<IAmazonSimpleSystemsManagement> _ssmMock;
    private readonly ConcurrentDictionary<string, (string Value, long Version)> _publishedParameters = new();
    private bool _ssmShouldFail;
    private string? _ssmFailureCode;
    private string? _ssmFailureMessage;

    public InMemoryRepository Repository { get; }

    // Real command implementations.
    public IRegisterCandidateCommand RegisterCandidate { get; }
    public IReconcileCommand Reconcile { get; }
    public IRollbackRingCommand RollbackRing { get; }
    public IPausePromotionsCommand PausePromotions { get; }
    public IResumePromotionsCommand ResumePromotions { get; }
    public IShowStateCommand ShowState { get; }

    // The real ManagedRingPublisher — tests the validation logic.
    public ManagedRingPublisher Publisher { get; }

    // ── Construction ──────────────────────────────────────────────────────

    public VerificationHarness(string parameterPrefix = "/test/release-rings/amis")
    {
        Repository = new InMemoryRepository();

        _ssmMock = new Mock<IAmazonSimpleSystemsManagement>();
        _ssmMock
            .Setup(s => s.PutParameterAsync(
                It.IsAny<PutParameterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PutParameterRequest req, CancellationToken _) =>
            {
                if (_ssmShouldFail)
                {
                    // Call .Result on a ValueTask — for a test harness this is safe
                    // (we control the synchronous failure path).
                    throw new AmazonSimpleSystemsManagementException(
                        _ssmFailureMessage ?? "Injected SSM failure");
                }

                var version = _publishedParameters.TryGetValue(req.Name, out var existing)
                    ? existing.Version + 1
                    : 1;
                _publishedParameters[req.Name] = (req.Value, version);
                return new PutParameterResponse { Version = version };
            });

        Publisher = new ManagedRingPublisher(
            _ssmMock.Object,
            parameterPrefix,
            Mock.Of<ILogger<ManagedRingPublisher>>());

        var metrics = new LoggingMetricsPublisher(
            Mock.Of<ILogger<LoggingMetricsPublisher>>());

        var ec2Validator = new PassThroughEc2Validator();

        RegisterCandidate = new RegisterCandidateCommand(
            Repository,
            Publisher,
            ec2Validator,
            metrics,
            Mock.Of<ILogger<RegisterCandidateCommand>>());

        Reconcile = new ReconcileCommand(
            Repository,
            Publisher,
            metrics,
            Mock.Of<ILogger<ReconcileCommand>>());

        RollbackRing = new RollbackRingCommand(
            Repository,
            Publisher,
            metrics,
            Mock.Of<ILogger<RollbackRingCommand>>());

        PausePromotions = new PausePromotionsCommand(
            Repository,
            metrics,
            Mock.Of<ILogger<PausePromotionsCommand>>());

        ResumePromotions = new ResumePromotionsCommand(
            Repository,
            metrics,
            Mock.Of<ILogger<ResumePromotionsCommand>>());

        ShowState = new ShowStateCommand(
            Repository,
            Mock.Of<ILogger<ShowStateCommand>>());
    }

    // ── Bootstrap ─────────────────────────────────────────────────────────

    public async Task BootstrapAsync(
        ImmutableDictionary<string, TimeSpan>? soakDurations = null,
        bool paused = false)
    {
        var config = new SystemConfig
        {
            Region = "us-east-1",
            ParameterPrefix = "/test/release-rings/amis",
            Rings = RingName.All,
            SoakDurations = soakDurations ?? new Dictionary<string, TimeSpan>
            {
                ["nightly->edge"] = TimeSpan.FromHours(1),
                ["edge->beta"] = TimeSpan.FromHours(24),
                ["beta->stable"] = TimeSpan.FromHours(72),
                ["stable->lts"] = TimeSpan.FromHours(168)
            }.ToImmutableDictionary(),
            IsPromotionPaused = paused,
            PauseReason = paused ? "bootstrapped-paused" : null
        };

        await Repository.SaveSystemConfigAsync(config, CancellationToken.None);
    }

    // ── Failure injection ─────────────────────────────────────────────────

    public void InjectSsmFailure(string? code = "SSM_PUBLISH_FAILED", string? message = "Injected SSM failure")
    {
        _ssmShouldFail = true;
        _ssmFailureCode = code;
        _ssmFailureMessage = message;
    }

    public void ClearSsmFailure()
    {
        _ssmShouldFail = false;
        _ssmFailureCode = null;
        _ssmFailureMessage = null;
    }

    // ── Published parameter inspection ────────────────────────────────────

    public (string Value, long Version)? GetPublishedParameter(string parameterName)
    {
        return _publishedParameters.TryGetValue(parameterName, out var p) ? p : null;
    }

    public IReadOnlyDictionary<string, (string Value, long Version)> AllPublishedParameters()
    {
        return new Dictionary<string, (string, long)>(_publishedParameters);
    }

    // ── SSM request inspection ────────────────────────────────────────────

    public PutParameterRequest? LastPutParameterRequest { get; private set; }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        // Nothing to clean up — all state is in-memory.
    }

    // ── Inner types ───────────────────────────────────────────────────────

    private sealed class PassThroughEc2Validator : IEc2ImageValidator
    {
        public Task<bool> AmiExistsAsync(string amiId, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> IsValidImageReferenceAsync(string amiId, CancellationToken ct) =>
            Task.FromResult(true);
    }
}
