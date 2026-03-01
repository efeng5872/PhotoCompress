using ImageMagick;
using PhotoCompress.Core.Models;
using System.Text;

namespace PhotoCompress.Core.Services;

public sealed class ImageCompressionService : IImageCompressionService
{
    private const int MinLossyQuality = 1;
    private const int MaxLossyQuality = 100;

    public Task<CompressResult> CompressAsync(CompressOptions options, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CompressInternal(options, cancellationToken), cancellationToken);
    }

    public Task<CompressEstimateResult> EstimateAsync(CompressOptions options, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => EstimateInternal(options, cancellationToken), cancellationToken);
    }

    private static CompressEstimateResult EstimateInternal(CompressOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            return CreateEstimateFailure("输入文件路径不能为空。");
        }

        if (options.TargetSizeKb <= 0)
        {
            return CreateEstimateFailure("目标大小必须大于 0 KB。");
        }

        if (options.ScalePercent <= 0)
        {
            return CreateEstimateFailure("缩放比例必须大于 0%。");
        }

        if (!File.Exists(options.InputPath))
        {
            return CreateEstimateFailure("输入文件不存在。");
        }

        var inputInfo = new FileInfo(options.InputPath);
        var targetBytes = options.TargetSizeKb * 1024L;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var image = new MagickImage(options.InputPath);
            var sourceFormat = image.Format;

            if (sourceFormat == MagickFormat.Unknown)
            {
                return CreateEstimateFailure("不支持或无法识别的图片格式。", inputInfo.Length, targetBytes);
            }

            var outputFormat = ResolveOutputFormat(sourceFormat, options.OutputFormatOverride);
            var suggestedScale = CalculateSuggestedScalePercent(inputInfo.Length, targetBytes);

            ApplyScaleIfNeeded(image, options.ScalePercent);
            image.Strip();

            var isLossy = IsLossyFormat(outputFormat);
            AttemptResult attempt = isLossy
                ? TryLossyBinarySearch(image, outputFormat, targetBytes, cancellationToken)
                : TryLosslessFormats(image, outputFormat, targetBytes, cancellationToken);

            if (!attempt.IsSuccess)
            {
                var failureMessage = BuildFailureMessage(
                    attempt,
                    targetBytes,
                    options.ScalePercent,
                    outputFormat,
                    outputFormat != sourceFormat);

                return new CompressEstimateResult
                {
                    IsSuccess = false,
                    IsTargetReached = false,
                    EstimatedOutputSizeBytes = attempt.OutputSizeBytes,
                    TargetSizeBytes = targetBytes,
                    OriginalSizeBytes = inputInfo.Length,
                    EstimatedQuality = attempt.Quality,
                    EstimatedWidth = (int)image.Width,
                    EstimatedHeight = (int)image.Height,
                    SuggestedScalePercent = suggestedScale,
                    Message = failureMessage,
                };
            }

            return new CompressEstimateResult
            {
                IsSuccess = true,
                IsTargetReached = attempt.OutputSizeBytes <= targetBytes,
                EstimatedOutputSizeBytes = attempt.OutputSizeBytes,
                TargetSizeBytes = targetBytes,
                OriginalSizeBytes = inputInfo.Length,
                EstimatedQuality = attempt.Quality,
                EstimatedWidth = (int)image.Width,
                EstimatedHeight = (int)image.Height,
                SuggestedScalePercent = suggestedScale,
                Message = "已生成预计结果。",
            };
        }
        catch (OperationCanceledException)
        {
            return CreateEstimateFailure("预估任务已取消。", inputInfo.Length, targetBytes);
        }
        catch (Exception ex)
        {
            return CreateEstimateFailure($"预估失败: {ex.Message}", inputInfo.Length, targetBytes);
        }
    }

    private static CompressResult CompressInternal(CompressOptions options, CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.Now;

        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            return CreateFailure(options, "输入文件路径不能为空。", start);
        }

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return CreateFailure(options, "输出文件路径不能为空。", start);
        }

        if (options.TargetSizeKb <= 0)
        {
            return CreateFailure(options, "目标大小必须大于 0 KB。", start);
        }

        if (options.ScalePercent <= 0)
        {
            return CreateFailure(options, "缩放比例必须大于 0%。", start);
        }

        if (!File.Exists(options.InputPath))
        {
            return CreateFailure(options, "输入文件不存在。", start);
        }

        var inputInfo = new FileInfo(options.InputPath);
        var targetBytes = options.TargetSizeKb * 1024L;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var image = new MagickImage(options.InputPath);
            var sourceFormat = image.Format;

            if (sourceFormat == MagickFormat.Unknown)
            {
                return CreateFailure(options, "不支持或无法识别的图片格式。", start, inputInfo.Length, 0, null, "unknown");
            }

            var outputFormat = ResolveOutputFormat(sourceFormat, options.OutputFormatOverride);

            ApplyScaleIfNeeded(image, options.ScalePercent);
            image.Strip();

            var isLossy = IsLossyFormat(outputFormat);
            AttemptResult attempt = isLossy
                ? TryLossyBinarySearch(image, outputFormat, targetBytes, cancellationToken)
                : TryLosslessFormats(image, outputFormat, targetBytes, cancellationToken);

            if (!attempt.IsSuccess || attempt.OutputBytes is null)
            {
                var reason = BuildFailureMessage(
                    attempt,
                    targetBytes,
                    options.ScalePercent,
                    outputFormat,
                    outputFormat != sourceFormat);

                return CreateFailure(
                    options,
                    reason,
                    start,
                    inputInfo.Length,
                    attempt.OutputSizeBytes,
                    attempt.Quality,
                    outputFormat.ToString().ToLowerInvariant());
            }

            EnsureOutputDirectory(options.OutputPath);
            File.WriteAllBytes(options.OutputPath, attempt.OutputBytes);

            return new CompressResult
            {
                IsSuccess = true,
                IsTargetReached = attempt.OutputSizeBytes <= targetBytes,
                OriginalSizeBytes = inputInfo.Length,
                OutputSizeBytes = attempt.OutputSizeBytes,
                AppliedQuality = attempt.Quality,
                Message = outputFormat == sourceFormat
                    ? "压缩成功。"
                    : $"压缩成功（已按用户决策转换为 {outputFormat.ToString().ToLowerInvariant()}）。",
                InputPath = options.InputPath,
                OutputPath = options.OutputPath,
                OutputFormat = outputFormat.ToString().ToLowerInvariant(),
                Elapsed = DateTimeOffset.Now - start,
            };
        }
        catch (OperationCanceledException)
        {
            return CreateFailure(
                options,
                "任务已取消。",
                start,
                inputInfo.Length,
                0,
                null,
                string.Empty);
        }
        catch (Exception ex)
        {
            return CreateFailure(
                options,
                $"压缩失败: {ex.Message}",
                start,
                inputInfo.Length,
                0,
                null,
                string.Empty);
        }
    }

    private static void ApplyScaleIfNeeded(MagickImage image, int scalePercent)
    {
        if (scalePercent == 100)
        {
            return;
        }

        var scaledWidth = Math.Max(1, (int)Math.Round(image.Width * scalePercent / 100.0));
        var scaledHeight = Math.Max(1, (int)Math.Round(image.Height * scalePercent / 100.0));

        image.Resize((uint)scaledWidth, (uint)scaledHeight);
    }

    private static AttemptResult TryLossyBinarySearch(
        MagickImage source,
        MagickFormat format,
        long targetBytes,
        CancellationToken cancellationToken)
    {
        byte[]? bestBytes = null;
        int? bestQuality = null;
        long bestSize = long.MaxValue;
        long minObservedSize = long.MaxValue;

        var low = MinLossyQuality;
        var high = MaxLossyQuality;

        while (low <= high)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mid = (low + high) / 2;
            var bytes = EncodeBytes(source, format, mid);
            var size = bytes.LongLength;
            if (size < minObservedSize)
            {
                minObservedSize = size;
            }

            if (size <= targetBytes)
            {
                bestBytes = bytes;
                bestQuality = mid;
                bestSize = size;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (bestBytes is null)
        {
            return AttemptResult.Fail("当前格式在不转格式条件下无法达到目标，需用户决策。", null, minObservedSize == long.MaxValue ? 0 : minObservedSize);
        }

        return AttemptResult.Success(bestBytes, bestQuality, bestSize);
    }

    private static AttemptResult TryLosslessFormats(
        MagickImage source,
        MagickFormat format,
        long targetBytes,
        CancellationToken cancellationToken)
    {
        switch (format)
        {
            case MagickFormat.Png:
            case MagickFormat.Png8:
            case MagickFormat.Png24:
            case MagickFormat.Png32:
            case MagickFormat.Png48:
            case MagickFormat.Png64:
            case MagickFormat.Png00:
            case MagickFormat.APng:
                return TryPngCompressionLevels(source, format, targetBytes, cancellationToken);
            case MagickFormat.Bmp:
            case MagickFormat.Bmp2:
            case MagickFormat.Bmp3:
                {
                    var bytes = EncodeBmpWithRle(source, format);
                    var size = bytes.LongLength;
                    if (size <= targetBytes)
                    {
                        return AttemptResult.Success(bytes, null, size);
                    }

                    return AttemptResult.Fail("当前格式在不转格式条件下无法达到目标，需用户决策。", null, size);
                }
            case MagickFormat.Tiff:
            case MagickFormat.Tiff64:
                return TryTiffCompressionMethods(source, format, targetBytes, cancellationToken);
            case MagickFormat.Gif:
            case MagickFormat.Gif87:
                {
                    var bytes = EncodeBytes(source, format, null);
                    var size = bytes.LongLength;
                    if (size <= targetBytes)
                    {
                        return AttemptResult.Success(bytes, null, size);
                    }

                    return AttemptResult.Fail("当前格式在不转格式条件下无法达到目标，需用户决策。", null, size);
                }
            default:
                {
                    var bytes = EncodeBytes(source, format, null);
                    var size = bytes.LongLength;
                    if (size <= targetBytes)
                    {
                        return AttemptResult.Success(bytes, null, size);
                    }

                    return AttemptResult.Fail("当前格式在不转格式条件下无法达到目标，需用户决策。", null, size);
                }
        }
    }

    private static AttemptResult TryPngCompressionLevels(
        MagickImage source,
        MagickFormat format,
        long targetBytes,
        CancellationToken cancellationToken)
    {
        byte[]? bestBytes = null;
        long bestSize = long.MaxValue;

        for (var level = 0; level <= 9; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = EncodePngWithLevel(source, format, level);
            var size = bytes.LongLength;
            if (size < bestSize)
            {
                bestBytes = bytes;
                bestSize = size;
            }
        }

        if (bestBytes is null)
        {
            return AttemptResult.Fail("PNG 压缩失败。", null, 0);
        }

        if (bestSize <= targetBytes)
        {
            return AttemptResult.Success(bestBytes, null, bestSize);
        }

        return AttemptResult.Fail("当前格式在不转格式条件下无法达到目标，需用户决策。", null, bestSize);
    }

    private static byte[] EncodePngWithLevel(MagickImage source, MagickFormat format, int compressionLevel)
    {
        using var candidate = source.Clone();
        candidate.Format = format;
        candidate.Settings.SetDefine(MagickFormat.Png, "compression-level", compressionLevel);
        return candidate.ToByteArray(format);
    }

    private static AttemptResult TryTiffCompressionMethods(
        MagickImage source,
        MagickFormat format,
        long targetBytes,
        CancellationToken cancellationToken)
    {
        byte[]? bestBytes = null;
        long bestSize = long.MaxValue;

        var methods = new[]
        {
            "Zip",
            "LZW",
            "RLE",
            "None",
        };

        foreach (var method in methods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = EncodeTiffWithCompression(source, format, method);
            var size = bytes.LongLength;
            if (size < bestSize)
            {
                bestBytes = bytes;
                bestSize = size;
            }
        }

        if (bestBytes is null)
        {
            return AttemptResult.Fail("TIFF 压缩失败。", null, 0);
        }

        if (bestSize <= targetBytes)
        {
            return AttemptResult.Success(bestBytes, null, bestSize);
        }

        return AttemptResult.Fail("当前格式在不转格式条件下无法达到目标，需用户决策。", null, bestSize);
    }

    private static byte[] EncodeBmpWithRle(MagickImage source, MagickFormat format)
    {
        using var candidate = source.Clone();
        candidate.Format = format;
        candidate.Settings.SetDefine(MagickFormat.Bmp, "compression", "RLE");
        return candidate.ToByteArray(format);
    }

    private static byte[] EncodeTiffWithCompression(MagickImage source, MagickFormat format, string compressionMethod)
    {
        using var candidate = source.Clone();
        candidate.Format = format;
        candidate.Settings.SetDefine(MagickFormat.Tiff, "compression", compressionMethod);
        return candidate.ToByteArray(format);
    }

    private static byte[] EncodeBytes(MagickImage source, MagickFormat format, int? quality)
    {
        using var candidate = source.Clone();
        candidate.Format = format;
        if (quality.HasValue)
        {
            candidate.Quality = (uint)quality.Value;
        }

        return candidate.ToByteArray(format);
    }

    private static bool IsLossyFormat(MagickFormat format)
    {
        return format switch
        {
            MagickFormat.Jpeg => true,
            MagickFormat.Jpg => true,
            MagickFormat.Jpe => true,
            MagickFormat.WebP => true,
            _ => false,
        };
    }

    private static MagickFormat ResolveOutputFormat(MagickFormat sourceFormat, string? outputFormatOverride)
    {
        if (string.IsNullOrWhiteSpace(outputFormatOverride))
        {
            return sourceFormat;
        }

        return outputFormatOverride.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => MagickFormat.Jpeg,
            "png" => MagickFormat.Png,
            "webp" => MagickFormat.WebP,
            "bmp" => MagickFormat.Bmp,
            "gif" => MagickFormat.Gif,
            "tif" or "tiff" => MagickFormat.Tiff,
            _ => sourceFormat,
        };
    }

    private static string BuildFailureMessage(
        AttemptResult attempt,
        long targetBytes,
        int currentScalePercent,
        MagickFormat attemptedFormat,
        bool isFormatConverted)
    {
        var sb = new StringBuilder();

        if (attempt.OutputSizeBytes > 0)
        {
            var minReachableKb = (long)Math.Ceiling(attempt.OutputSizeBytes / 1024d);
            var targetKb = (long)Math.Ceiling(targetBytes / 1024d);
            sb.Append($"当前{attemptedFormat.ToString().ToLowerInvariant()}格式在缩放 {currentScalePercent}% 下，最小可达约 {minReachableKb}KB，无法满足 {targetKb}KB。 ");
        }
        else
        {
            sb.Append($"当前{attemptedFormat.ToString().ToLowerInvariant()}格式在缩放 {currentScalePercent}% 下仍无法达到目标。 ");
        }

        var suggestedScale = CalculateSuggestedScaleByAttempt(attempt.OutputSizeBytes, targetBytes, currentScalePercent);
        if (suggestedScale < currentScalePercent)
        {
            sb.Append($"建议把缩放比例从 {currentScalePercent}% 调整到约 {suggestedScale}% 后重试；或提高目标大小。");
        }
        else
        {
            sb.Append("建议提高目标大小后重试。");
        }

        if (!isFormatConverted)
        {
            sb.Append(" 你也可以选择转换为其他格式（如 JPEG/WebP）以提高达标概率。 ");
        }

        if (!string.IsNullOrWhiteSpace(attempt.Message) && !attempt.Message.Contains("无法达到目标", StringComparison.Ordinal))
        {
            sb.Append($"附加信息：{attempt.Message}");
        }

        return sb.ToString().Trim();
    }

    private static int CalculateSuggestedScaleByAttempt(long minOutputBytes, long targetBytes, int currentScalePercent)
    {
        if (minOutputBytes <= 0 || targetBytes <= 0)
        {
            return Math.Clamp(currentScalePercent, 1, 100);
        }

        if (minOutputBytes <= targetBytes)
        {
            return Math.Clamp(currentScalePercent, 1, 100);
        }

        var ratio = Math.Sqrt((double)targetBytes / minOutputBytes);
        var safety = ratio * 0.95;
        var suggested = (int)Math.Floor(currentScalePercent * safety);
        return Math.Clamp(suggested, 1, currentScalePercent);
    }

    private static int CalculateSuggestedScalePercent(long originalBytes, long targetBytes)
    {
        if (originalBytes <= 0 || targetBytes <= 0)
        {
            return 100;
        }

        if (originalBytes <= targetBytes)
        {
            return 100;
        }

        var ratio = Math.Sqrt((double)targetBytes / originalBytes);
        var safetyRatio = ratio * 0.95;
        var suggested = (int)Math.Floor(safetyRatio * 100);

        return Math.Clamp(suggested, 10, 100);
    }

    private static CompressEstimateResult CreateEstimateFailure(string message, long originalBytes = 0, long targetBytes = 0)
    {
        return new CompressEstimateResult
        {
            IsSuccess = false,
            IsTargetReached = false,
            EstimatedOutputSizeBytes = 0,
            TargetSizeBytes = targetBytes,
            OriginalSizeBytes = originalBytes,
            EstimatedQuality = null,
            EstimatedWidth = 0,
            EstimatedHeight = 0,
            SuggestedScalePercent = 100,
            Message = message,
        };
    }

    private static void EnsureOutputDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static CompressResult CreateFailure(
        CompressOptions options,
        string message,
        DateTimeOffset start,
        long originalBytes = 0,
        long outputBytes = 0,
        int? quality = null,
        string format = "")
    {
        return new CompressResult
        {
            IsSuccess = false,
            IsTargetReached = false,
            OriginalSizeBytes = originalBytes,
            OutputSizeBytes = outputBytes,
            AppliedQuality = quality,
            Message = message,
            InputPath = options.InputPath,
            OutputPath = options.OutputPath,
            OutputFormat = format,
            Elapsed = DateTimeOffset.Now - start,
        };
    }

    private sealed record AttemptResult(bool IsSuccess, byte[]? OutputBytes, int? Quality, long OutputSizeBytes, string? Message)
    {
        public static AttemptResult Success(byte[] bytes, int? quality, long size)
            => new(true, bytes, quality, size, null);

        public static AttemptResult Fail(string message, int? quality, long size)
            => new(false, null, quality, size, message);
    }
}
