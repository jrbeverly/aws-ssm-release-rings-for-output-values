namespace ReleaseRingService.Core.Models;

public sealed record RingTransition
{
    public required RingName From { get; init; }
    public required RingName To { get; init; }
    public required TimeSpan SoakDuration { get; init; }

    public string Key => $"{From.Value}->{To.Value}";

    public static IReadOnlyList<RingTransition> BuildFromRingOrder(
        IReadOnlyList<RingName> rings,
        IReadOnlyDictionary<string, TimeSpan> soakDurations)
    {
        var transitions = new List<RingTransition>(rings.Count - 1);
        for (int i = 0; i < rings.Count - 1; i++)
        {
            var key = $"{rings[i].Value}->{rings[i + 1].Value}";
            var duration = soakDurations.TryGetValue(key, out var d)
                ? d
                : TimeSpan.Zero;
            transitions.Add(new RingTransition
            {
                From = rings[i],
                To = rings[i + 1],
                SoakDuration = duration
            });
        }
        return transitions;
    }
}
