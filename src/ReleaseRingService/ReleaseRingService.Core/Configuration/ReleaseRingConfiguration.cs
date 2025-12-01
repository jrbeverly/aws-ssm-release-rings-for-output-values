using System.ComponentModel.DataAnnotations;

namespace ReleaseRingService.Core.Configuration;

public sealed class ReleaseRingConfiguration
{
    public const string SectionName = "ReleaseRing";

    [Required]
    public string Region { get; set; } = string.Empty;

    [Required]
    public string ParameterPrefix { get; set; } = "/release-rings/amis";

    [Required, MinLength(2)]
    public string[] Rings { get; set; } = ["nightly", "edge", "beta", "stable", "lts"];

    [Required]
    public Dictionary<string, string> SoakDurations { get; set; } = new();

    public bool IsPromotionPaused { get; set; }

    [Required]
    public string TableName { get; set; } = "ReleaseRingState";
}
