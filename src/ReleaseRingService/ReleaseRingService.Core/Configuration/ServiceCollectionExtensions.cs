using Microsoft.Extensions.DependencyInjection;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Models;

namespace ReleaseRingService.Core.Configuration;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all core release-ring domain services.
    /// Both the scheduled worker and operator CLI call this to share
    /// the same configuration loading, logging bootstrap, and command
    /// resolution path.
    /// </summary>
    public static IServiceCollection AddReleaseRingService(
        this IServiceCollection services)
    {
        // Command interfaces — consumed by both entrypoints.
        // Implementations are filled in by later issues.
        services.AddTransient<IRegisterCandidateCommand, StubRegisterCandidateCommand>();
        services.AddTransient<IReconcileCommand, StubReconcileCommand>();
        services.AddTransient<IRollbackRingCommand, StubRollbackRingCommand>();
        services.AddTransient<IPausePromotionsCommand, StubPausePromotionsCommand>();
        services.AddTransient<IResumePromotionsCommand, StubResumePromotionsCommand>();
        services.AddTransient<IShowStateCommand, StubShowStateCommand>();

        return services;
    }
}

// Stub implementations that satisfy the interfaces during scaffold.
// Later issues replace these with real domain logic.

file sealed class StubRegisterCandidateCommand : IRegisterCandidateCommand
{
    public Task<Result<RegisterCandidateResult>> ExecuteAsync(
        string amiId, string? sourceMetadata, CancellationToken ct)
    {
        return Task.FromResult(Result<RegisterCandidateResult>.Failure(
            new Error("NOT_IMPLEMENTED", "RegisterCandidate is not yet implemented.")));
    }
}

file sealed class StubReconcileCommand : IReconcileCommand
{
    public Task<Result<ReconcileResult>> ExecuteAsync(CancellationToken ct)
    {
        return Task.FromResult(Result<ReconcileResult>.Failure(
            new Error("NOT_IMPLEMENTED", "Reconcile is not yet implemented.")));
    }
}

file sealed class StubRollbackRingCommand : IRollbackRingCommand
{
    public Task<Result<RingState>> ExecuteAsync(
        RingName ringName, string targetAmiId, string reason, CancellationToken ct)
    {
        return Task.FromResult(Result<RingState>.Failure(
            new Error("NOT_IMPLEMENTED", "RollbackRing is not yet implemented.")));
    }
}

file sealed class StubPausePromotionsCommand : IPausePromotionsCommand
{
    public Task<Result> ExecuteAsync(string reason, CancellationToken ct)
    {
        return Task.FromResult(Result.Failure(
            new Error("NOT_IMPLEMENTED", "PausePromotions is not yet implemented.")));
    }
}

file sealed class StubResumePromotionsCommand : IResumePromotionsCommand
{
    public Task<Result> ExecuteAsync(CancellationToken ct)
    {
        return Task.FromResult(Result.Failure(
            new Error("NOT_IMPLEMENTED", "ResumePromotions is not yet implemented.")));
    }
}

file sealed class StubShowStateCommand : IShowStateCommand
{
    public Task<Result<ShowStateResult>> ExecuteAsync(CancellationToken ct)
    {
        return Task.FromResult(Result<ShowStateResult>.Failure(
            new Error("NOT_IMPLEMENTED", "ShowState is not yet implemented.")));
    }
}
