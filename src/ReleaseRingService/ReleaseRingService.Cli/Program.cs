using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReleaseRingService.Core.Commands;
using ReleaseRingService.Core.Errors;
using ReleaseRingService.Core.Models;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole();
builder.Services.AddCliReleaseRingService();
builder.Services.AddCliReleaseRingInfrastructure(builder.Configuration);

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("CLI");

var command = args.Length > 0 ? args[0] : "--help";

switch (command)
{
    case "register-candidate":
        {
            var amiId = args.ElementAtOrDefault(1);
            if (amiId is null)
            {
                Console.Error.WriteLine("Usage: rrc register-candidate <ami-id> [source-metadata]");
                break;
            }

            var metadata = args.ElementAtOrDefault(2);
            var cmd = host.Services.GetRequiredService<IRegisterCandidateCommand>();
            var result = await cmd.ExecuteAsync(amiId, metadata, CancellationToken.None);

            if (result.IsSuccess)
            {
                var data = result.Value!;
                if (data.Outcome == RegistrationOutcome.AlreadyKnown)
                    logger.LogInformation(
                        "Candidate already registered: {AmiId} (registered at {Timestamp})",
                        data.Candidate.AmiId, data.Candidate.RegistrationTimestamp);
                else
                    logger.LogInformation("Registered candidate: {AmiId}", data.Candidate.AmiId);
            }
            else
            {
                logger.LogError("Registration failed: {Error}", result.Error);
                Environment.ExitCode = 1;
            }

            break;
        }

    case "reconcile":
        {
            var cmd = host.Services.GetRequiredService<IReconcileCommand>();
            var result = await cmd.ExecuteAsync(CancellationToken.None);

            if (result.IsSuccess)
            {
                var data = result.Value!;
                logger.LogInformation(
                    "Reconciliation complete. Promoted: {PromotedCount}, Skipped: {SkippedCount}",
                    data.PromotedRings.Count, data.SkippedRings.Count);
            }
            else
            {
                logger.LogError("Reconciliation failed: {Error}", result.Error);
                Environment.ExitCode = 1;
            }

            break;
        }

    case "rollback-ring":
        {
            var ringNameStr = args.ElementAtOrDefault(1);
            var targetAmiId = args.ElementAtOrDefault(2);
            var reason = args.ElementAtOrDefault(3);

            if (ringNameStr is null || targetAmiId is null || reason is null)
            {
                Console.Error.WriteLine(
                    "Usage: rrc rollback-ring <ring-name> <ami-id> <reason>");
                break;
            }

            var ringName = RingName.From(ringNameStr);
            if (ringName is null)
            {
                logger.LogError("Unknown ring: {RingName}", ringNameStr);
                Environment.ExitCode = 1;
                break;
            }

            var cmd = host.Services.GetRequiredService<IRollbackRingCommand>();
            var result = await cmd.ExecuteAsync(ringName, targetAmiId, reason, CancellationToken.None);

            if (result.IsSuccess)
                logger.LogInformation("Rolled back {RingName} to {AmiId}", ringName, targetAmiId);
            else
            {
                logger.LogError("Rollback failed: {Error}", result.Error);
                Environment.ExitCode = 1;
            }

            break;
        }

    case "pause-promotions":
        {
            var reason = args.ElementAtOrDefault(1) ?? "Operator paused via CLI";
            var cmd = host.Services.GetRequiredService<IPausePromotionsCommand>();
            var result = await cmd.ExecuteAsync(reason, CancellationToken.None);

            if (result.IsSuccess)
                logger.LogInformation("Promotions paused");
            else
            {
                logger.LogError("Pause failed: {Error}", result.Error);
                Environment.ExitCode = 1;
            }

            break;
        }

    case "resume-promotions":
        {
            var cmd = host.Services.GetRequiredService<IResumePromotionsCommand>();
            var result = await cmd.ExecuteAsync(CancellationToken.None);

            if (result.IsSuccess)
                logger.LogInformation("Promotions resumed");
            else
            {
                logger.LogError("Resume failed: {Error}", result.Error);
                Environment.ExitCode = 1;
            }

            break;
        }

    case "show-state":
        {
            var cmd = host.Services.GetRequiredService<IShowStateCommand>();
            var result = await cmd.ExecuteAsync(CancellationToken.None);

            if (result.IsSuccess)
            {
                var state = result.Value!;
                Console.WriteLine($"Promotion paused: {state.IsPromotionPaused}");
                if (state.PauseReason is not null)
                    Console.WriteLine($"Pause reason: {state.PauseReason}");
                Console.WriteLine();
                Console.WriteLine("Ring state:");
                foreach (var ring in state.RingStates)
                {
                    var previous = ring.PreviousAmiId is not null
                        ? $"  (previous: {ring.PreviousAmiId} at {ring.PreviousPublishedAt:O})"
                        : "";
                    Console.WriteLine(
                        $"  {ring.RingName.Value}: {ring.CurrentAmiId} (since {ring.CurrentPublishedAt:O}){previous}");
                }
                if (state.RecentTransitions.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Recent transitions:");
                    foreach (var t in state.RecentTransitions)
                    {
                        var ringPart = t.RingName is not null ? $" ring={t.RingName.Value}" : "";
                        var amiPart = t.AmiId is not null ? $" ami={t.AmiId}" : "";
                        var reasonPart = t.OperatorReason is not null ? $" reason={t.OperatorReason}" : "";
                        Console.WriteLine(
                            $"  [{t.Timestamp:O}] {t.Type}{ringPart}{amiPart}{reasonPart}");
                    }
                }
            }
            else
            {
                logger.LogError("Show-state failed: {Error}", result.Error);
                Environment.ExitCode = 1;
            }

            break;
        }

    case "--help":
    case "-h":
    default:
        Console.WriteLine("release-rings-control-plane — CLI operator interface");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  register-candidate <ami-id> [source-metadata]");
        Console.WriteLine("  reconcile");
        Console.WriteLine("  rollback-ring <ring-name> <ami-id> <reason>");
        Console.WriteLine("  pause-promotions [reason]");
        Console.WriteLine("  resume-promotions");
        Console.WriteLine("  show-state");
        break;
}

// Extension methods that delegate to Core and Infrastructure composition roots.
// Both Worker and CLI share the same canonical registration paths.
static class CliServiceCollectionExtensions
{
    public static IServiceCollection AddCliReleaseRingService(
        this IServiceCollection services) =>
        ReleaseRingService.Core.Configuration.ServiceCollectionExtensions.AddReleaseRingService(services);

    public static IServiceCollection AddCliReleaseRingInfrastructure(
        this IServiceCollection services, IConfiguration configuration) =>
        ReleaseRingService.Infrastructure.Configuration.ServiceCollectionExtensions.AddReleaseRingInfrastructure(services, configuration);
}
