using System.ComponentModel.DataAnnotations;

namespace VoicesIntegration.Infrastructure.Recovery;

public sealed class RecoveryOptions
{
    public const string SectionName = "Recovery";

    public string Path { get; init; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoicesIntegration", "recuperacion");

    [Range(1, 20)]
    public int MaxFileGb { get; init; } = 6;

    [Range(1, 720)]
    public int ReviewedExpireHours { get; init; } = 48;
}
