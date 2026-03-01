namespace PhotoCompress.Core.Models;

public sealed class CompressEstimateResult
{
    public bool IsSuccess { get; init; }

    public bool IsTargetReached { get; init; }

    public long EstimatedOutputSizeBytes { get; init; }

    public long TargetSizeBytes { get; init; }

    public long OriginalSizeBytes { get; init; }

    public int? EstimatedQuality { get; init; }

    public int EstimatedWidth { get; init; }

    public int EstimatedHeight { get; init; }

    public int SuggestedScalePercent { get; init; }

    public string? Message { get; init; }
}
