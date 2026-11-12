using System.ComponentModel.DataAnnotations;

namespace VoicesIntegration.Infrastructure.Inbox;

public sealed class InboxOptions
{
    public const string SectionName = "Inbox";

    public string Path { get; init; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoicesIntegration", "inbox");

    [Range(1, 20000)]
    public int BatchSize { get; init; } = 500;

    [Range(1, 32)]
    public int MaxDegreeOfParallelism { get; init; } = 4;

    [Range(1, 8)]
    public int SqlParallel { get; init; } = 1;

    [Range(100, 10000)]
    public int SqlBatchSize { get; init; } = 1000;

    [Range(1, 600)]
    public int MaxRetryDelaySeconds { get; init; } = 60;

    [Range(1, 365)]
    public int RejectedRetentionDays { get; init; } = 30;

    [Range(100, 1_000_000)]
    public int MinFreeDiskMb { get; init; } = 2048;

    [Range(100, 100_000)]
    public int SegmentMaxItems { get; init; } = 5000;

    [Range(1, 300)]
    public int SegmentMaxSeconds { get; init; } = 10;

    [Range(1, 1440)]
    public int MaxPendingAgeMinutes { get; init; } = 5;
}
