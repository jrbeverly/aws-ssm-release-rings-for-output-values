using ReleaseRingService.Core.Models;

namespace ReleaseRingService.Infrastructure.DynamoDb.Keys;

public static class DynamoKeyScheme
{
    // ── SystemConfig (singleton row) ──
    public const string SystemConfigPk = "SYSTEM";
    public const string SystemConfigSk = "CONFIG";

    // ── Candidate ──
    public static string CandidatePk(string amiId) => $"CANDIDATE#{amiId}";
    public static string CandidateSk(string amiId) => $"CANDIDATE#{amiId}";

    // ── RingState ──
    public static string RingStatePk(string ringName) => $"RING#{ringName}";
    public static string RingStateSk(string ringName) => $"RING#{ringName}";

    // ── TransitionRecord (append-only history) ──
    public static string TransitionPk(string ringName) => $"HISTORY#{ringName}";
    public static string TransitionSk(DateTime timestamp, string ulid) =>
        $"{timestamp:O}#{ulid}";

    // ── GSI1: Candidate by lifecycle status ──
    public static string Gsi1StatusPk(CandidateLifecycleStatus status) => $"STATUS#{status}";
    public static string Gsi1StatusSk(DateTime registrationTimestamp) =>
        $"REGISTERED#{registrationTimestamp:O}";

    // ── GSI1: TransitionRecord by type (inverted from PK) ──
    // Reuses the entity's SK as GSI1PK so all transition types for a ring can be queried.
    public const string Gsi1Name = "GSI1";
}
