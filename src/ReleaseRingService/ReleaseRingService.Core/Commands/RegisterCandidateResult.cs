using ReleaseRingService.Core.Models;

namespace ReleaseRingService.Core.Commands;

public enum RegistrationOutcome
{
    Created,
    AlreadyKnown
}

public sealed record RegisterCandidateResult
{
    public required RegistrationOutcome Outcome { get; init; }
    public required Candidate Candidate { get; init; }
}
