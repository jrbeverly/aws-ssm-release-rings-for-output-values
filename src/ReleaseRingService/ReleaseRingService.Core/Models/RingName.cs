using System.Collections.Immutable;

namespace ReleaseRingService.Core.Models;

public sealed record RingName
{
    public static readonly RingName Nightly = new("nightly");
    public static readonly RingName Edge = new("edge");
    public static readonly RingName Beta = new("beta");
    public static readonly RingName Stable = new("stable");
    public static readonly RingName Lts = new("lts");

    public static readonly ImmutableArray<RingName> All = [
        Nightly, Edge, Beta, Stable, Lts
    ];

    public string Value { get; }

    private RingName(string value) => Value = value;

    public static RingName? From(string value) =>
        All.FirstOrDefault(r => r.Value.Equals(value, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Value;
}
