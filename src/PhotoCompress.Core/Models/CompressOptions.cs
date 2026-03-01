namespace PhotoCompress.Core.Models;

public sealed class CompressOptions
{
    public string InputPath { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;

    public long TargetSizeKb { get; init; }

    public int ScalePercent { get; init; } = 100;

    public string? OutputFormatOverride { get; init; }
}
