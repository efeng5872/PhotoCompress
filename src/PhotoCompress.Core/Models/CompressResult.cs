namespace PhotoCompress.Core.Models;

public sealed class CompressResult
{
    public bool IsSuccess { get; init; }

    public bool IsTargetReached { get; init; }

    public long OriginalSizeBytes { get; init; }

    public long OutputSizeBytes { get; init; }

    public int? AppliedQuality { get; init; }

    public string? Message { get; init; }

    public string InputPath { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;

    public string OutputFormat { get; init; } = string.Empty;

    public TimeSpan Elapsed { get; init; }
}
