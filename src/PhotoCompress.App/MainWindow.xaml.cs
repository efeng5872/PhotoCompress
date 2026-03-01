using Microsoft.Win32;
using PhotoCompress.Core.Models;
using PhotoCompress.Core.Services;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoCompress.App;

public partial class MainWindow : Window
{
    private const long DefaultTargetMaxKb = 10240;
    private readonly IImageCompressionService _compressionService;
    private CancellationTokenSource? _estimateCts;
    private bool _isUpdatingControls;
    private bool _isViewReady;
    private bool _isCompressing;
    private bool _shouldAutoApplySuggestedScale = true;

    public MainWindow()
    {
        InitializeComponent();
        _compressionService = new ImageCompressionService();

        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute));

        TargetSizeSlider.Minimum = 1;
        TargetSizeSlider.Maximum = DefaultTargetMaxKb;
        TargetRangeHintTextBlock.Text = $"滑块范围：1 ~ {DefaultTargetMaxKb} KB（可手工输入超出范围）";

        SetTargetSizeControl(300, fromUser: false, triggerEstimate: false);
        SetScalePercentControl(100, fromUser: false, triggerEstimate: false);
        SuggestedScaleTextBlock.Text = "建议缩放：--（可手工修改）";
        RenderEstimatePlain("请选择输入文件后查看实时预估。");
        RenderResult("尚未执行压缩。", isFailure: false);

        _isViewReady = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _estimateCts?.Cancel();
        _estimateCts?.Dispose();
        base.OnClosed(e);
    }

    private void OnBrowseInputClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        InputPathTextBox.Text = dialog.FileName;
        OutputPathTextBox.Text = BuildDefaultOutputPath(dialog.FileName);

        var selectedFormatOverride = GetSelectedOutputFormatOverride();
        if (!string.IsNullOrWhiteSpace(selectedFormatOverride))
        {
            AlignOutputPathExtensionWithSelectedFormat(selectedFormatOverride);
        }

        ApplyAdaptiveTargetRange(dialog.FileName);
        _shouldAutoApplySuggestedScale = true;

        Log.Information("Input file selected: {InputPath}", dialog.FileName);
        Log.Information("Output path auto-updated for selected input: {OutputPath}", OutputPathTextBox.Text);

        _ = RefreshEstimateAsync();
    }

    private void OnBrowseOutputClick(object sender, RoutedEventArgs e)
    {
        var inputPath = InputPathTextBox.Text.Trim();
        var selectedFormatOverride = GetSelectedOutputFormatOverride();

        var extension = !string.IsNullOrWhiteSpace(selectedFormatOverride)
            ? GetPreferredExtensionByFormat(selectedFormatOverride)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
            if (!string.IsNullOrWhiteSpace(inputPath))
            {
                extension = Path.GetExtension(inputPath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".jpg";
                }
            }
        }

        var defaultOutputPath = !string.IsNullOrWhiteSpace(inputPath)
            ? BuildDefaultOutputPath(inputPath)
            : "compressed" + extension;

        if (!string.IsNullOrWhiteSpace(selectedFormatOverride))
        {
            defaultOutputPath = GetPathWithPreferredExtension(defaultOutputPath, selectedFormatOverride);
        }

        var dialog = new SaveFileDialog
        {
            Title = "选择输出文件",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*",
            AddExtension = true,
            OverwritePrompt = true,
            DefaultExt = extension,
            FileName = Path.GetFileName(defaultOutputPath),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        OutputPathTextBox.Text = dialog.FileName;

        if (!string.IsNullOrWhiteSpace(selectedFormatOverride))
        {
            AlignOutputPathExtensionWithSelectedFormat(selectedFormatOverride);
        }

        Log.Information("Output path selected: {OutputPath}, OutputFormatOverride={OutputFormatOverride}", OutputPathTextBox.Text, selectedFormatOverride ?? "auto");

        _ = RefreshEstimateAsync();
    }

    private async void OnCompressClick(object sender, RoutedEventArgs e)
    {
        InfoTabControl.SelectedIndex = 1;

        if (!TryBuildOptions(out var options, out var validationMessage))
        {
            RenderResult(validationMessage, isFailure: true);
            MessageBox.Show(validationMessage, "参数校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Warning("Validation failed: {Reason}", validationMessage);
            return;
        }

        RenderResult("正在压缩，请稍候...", isFailure: false);

        _isCompressing = true;
        InfoTabControl.IsEnabled = false;
        StartCompressButton.IsEnabled = false;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var inputSize = new FileInfo(options.InputPath).Length;
            Log.Information(
                "Compression started. Input={InputPath}, Output={OutputPath}, OriginalBytes={OriginalBytes}, TargetKb={TargetKb}, ScalePercent={ScalePercent}, OutputFormatOverride={OutputFormatOverride}",
                options.InputPath,
                options.OutputPath,
                inputSize,
                options.TargetSizeKb,
                options.ScalePercent,
                options.OutputFormatOverride ?? "auto");

            var result = await _compressionService.CompressAsync(options);
            stopwatch.Stop();

            LogResult(options, result, stopwatch.Elapsed);
            RenderFinalResult(result, options.TargetSizeKb);

            if (!result.IsSuccess)
            {
                MessageBox.Show(result.Message ?? "压缩失败。", "压缩结果", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show("压缩完成。", "压缩结果", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Error(ex, "Compression crashed. Input={InputPath}, Output={OutputPath}", options.InputPath, options.OutputPath);
            RenderResult($"失败：{ex.Message}", isFailure: true);
            MessageBox.Show($"压缩失败：{ex.Message}", "异常", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isCompressing = false;
            InfoTabControl.IsEnabled = true;
            StartCompressButton.IsEnabled = true;
        }
    }

    private void ShowEstimateTab()
    {
        if (_isCompressing)
        {
            return;
        }

        InfoTabControl.SelectedIndex = 0;
    }

    private void OnInfoTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isCompressing)
        {
            return;
        }

        if (InfoTabControl.SelectedIndex != 1)
        {
            InfoTabControl.SelectedIndex = 1;
        }
    }

    private void OnOutputFormatSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingControls || !_isViewReady)
        {
            return;
        }

        ShowEstimateTab();

        var selectedFormatOverride = GetSelectedOutputFormatOverride();
        if (!string.IsNullOrWhiteSpace(selectedFormatOverride))
        {
            AlignOutputPathExtensionWithSelectedFormat(selectedFormatOverride);
        }
        else
        {
            AlignOutputPathExtensionWithInputFormat();
        }

        _ = RefreshEstimateAsync();
    }

    private void OnTargetSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingControls || !_isViewReady)
        {
            return;
        }

        ShowEstimateTab();
        SetTargetSizeControl((long)Math.Round(e.NewValue), fromUser: true);
    }

    private void OnTargetSizeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingControls || !_isViewReady)
        {
            return;
        }

        ShowEstimateTab();

        if (TryParsePositiveLong(TargetSizeKbTextBox.Text, out var targetKb))
        {
            SetTargetSizeControl(targetKb, fromUser: true);
            return;
        }

        _ = RefreshEstimateAsync();
    }

    private void OnScalePercentSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingControls || !_isViewReady)
        {
            return;
        }

        ShowEstimateTab();
        SetScalePercentControl((int)Math.Round(e.NewValue), fromUser: true);
    }

    private void OnScalePercentTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingControls || !_isViewReady)
        {
            return;
        }

        ShowEstimateTab();

        if (TryParsePositiveInt(ScalePercentTextBox.Text, out var scalePercent))
        {
            SetScalePercentControl(scalePercent, fromUser: true);
            return;
        }

        _ = RefreshEstimateAsync();
    }

    private void SetTargetSizeControl(long value, bool fromUser, bool triggerEstimate = true)
    {
        value = Math.Max(1, value);

        _isUpdatingControls = true;
        TargetSizeKbTextBox.Text = value.ToString(CultureInfo.InvariantCulture);
        TargetSizeSlider.Value = ClampToSliderRange(value, TargetSizeSlider);
        _isUpdatingControls = false;

        if (fromUser)
        {
            _shouldAutoApplySuggestedScale = true;
        }

        if (triggerEstimate)
        {
            _ = RefreshEstimateAsync();
        }
    }

    private void SetScalePercentControl(int value, bool fromUser, bool triggerEstimate = true)
    {
        value = Math.Max(1, value);

        _isUpdatingControls = true;
        ScalePercentTextBox.Text = value.ToString(CultureInfo.InvariantCulture);
        ScalePercentSlider.Value = ClampToSliderRange(value, ScalePercentSlider);
        _isUpdatingControls = false;

        if (fromUser)
        {
            _shouldAutoApplySuggestedScale = false;
        }

        if (triggerEstimate)
        {
            _ = RefreshEstimateAsync();
        }
    }

    private async Task RefreshEstimateAsync()
    {
        _estimateCts?.Cancel();
        _estimateCts?.Dispose();

        if (!TryBuildEstimateOptions(out var estimateOptions, out var invalidReason))
        {
            RenderEstimatePlain(invalidReason);
            SuggestedScaleTextBlock.Text = "建议缩放：--（可手工修改）";
            return;
        }

        var cts = new CancellationTokenSource();
        _estimateCts = cts;

        try
        {
            await Task.Delay(180, cts.Token);
            var estimate = await _compressionService.EstimateAsync(estimateOptions, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            SuggestedScaleTextBlock.Text = $"建议缩放：{estimate.SuggestedScalePercent}%（可手工修改）";
            RenderEstimate(estimate);

            if (_shouldAutoApplySuggestedScale && estimate.SuggestedScalePercent > 0)
            {
                _shouldAutoApplySuggestedScale = false;

                if (!TryParsePositiveInt(ScalePercentTextBox.Text, out var currentScale) || currentScale != estimate.SuggestedScalePercent)
                {
                    SetScalePercentControl(estimate.SuggestedScalePercent, fromUser: false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Estimate refresh failed.");
            RenderEstimatePlain($"预计失败：{ex.Message}");
            SuggestedScaleTextBlock.Text = "建议缩放：--（可手工修改）";
        }
        finally
        {
            if (ReferenceEquals(_estimateCts, cts))
            {
                _estimateCts = null;
            }

            cts.Dispose();
        }
    }

    private bool TryBuildEstimateOptions(out CompressOptions options, out string message)
    {
        var inputPath = InputPathTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            options = new CompressOptions();
            message = "请选择输入文件后查看预计结果。";
            return false;
        }

        if (!File.Exists(inputPath))
        {
            options = new CompressOptions();
            message = "输入文件不存在，无法生成预计结果。";
            return false;
        }

        if (!TryParsePositiveLong(TargetSizeKbTextBox.Text.Trim(), out var targetKb))
        {
            options = new CompressOptions();
            message = "请输入大于 0 的目标大小（KB）以查看预计结果。";
            return false;
        }

        if (!TryParsePositiveInt(ScalePercentTextBox.Text.Trim(), out var scalePercent))
        {
            options = new CompressOptions();
            message = "请输入大于 0 的缩放比例（%）以查看预计结果。";
            return false;
        }

        var outputPath = OutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = BuildDefaultOutputPath(inputPath);
        }

        var selectedFormatOverride = GetSelectedOutputFormatOverride();

        options = new CompressOptions
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            TargetSizeKb = targetKb,
            ScalePercent = scalePercent,
            OutputFormatOverride = selectedFormatOverride,
        };

        message = string.Empty;
        return true;
    }

    private void ApplyAdaptiveTargetRange(string inputPath)
    {
        if (!File.Exists(inputPath))
        {
            return;
        }

        var inputBytes = new FileInfo(inputPath).Length;
        var inputKb = Math.Max(1L, (long)Math.Ceiling(inputBytes / 1024d));

        var adaptiveMax = Math.Max(1024L, inputKb * 2);
        adaptiveMax = Math.Min(adaptiveMax, 1024L * 1024L);

        TargetSizeSlider.Maximum = adaptiveMax;
        TargetRangeHintTextBlock.Text = $"滑块范围：1 ~ {adaptiveMax} KB（可手工输入超出范围）";

        if (TryParsePositiveLong(TargetSizeKbTextBox.Text, out var targetKb))
        {
            _isUpdatingControls = true;
            TargetSizeSlider.Value = ClampToSliderRange(targetKb, TargetSizeSlider);
            _isUpdatingControls = false;
        }
    }

    private static double ClampToSliderRange(long value, Slider slider)
    {
        var clamped = Math.Clamp(value, (long)slider.Minimum, (long)slider.Maximum);
        return clamped;
    }

    private static double ClampToSliderRange(int value, Slider slider)
    {
        var clamped = Math.Clamp(value, (int)slider.Minimum, (int)slider.Maximum);
        return clamped;
    }

    private static string BuildDefaultOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var extension = Path.GetExtension(inputPath);

        var candidate = fileName + "_compressed";
        var fullPath = Path.Combine(directory, $"{candidate}{extension}");
        if (!File.Exists(fullPath))
        {
            return fullPath;
        }

        for (var index = 2; index <= 9999; index++)
        {
            candidate = fileName + $"_compressed_{index}";
            fullPath = Path.Combine(directory, $"{candidate}{extension}");
            if (!File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return Path.Combine(directory, $"{fileName}_compressed_{DateTime.Now:yyyyMMddHHmmss}{extension}");
    }

    private string? GetSelectedOutputFormatOverride()
    {
        if (OutputFormatComboBox.SelectedItem is not ComboBoxItem selectedItem)
        {
            return null;
        }

        var tag = selectedItem.Tag?.ToString()?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private static string GetPreferredExtensionByFormat(string format)
    {
        return format.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => ".jpg",
            "png" => ".png",
            "webp" => ".webp",
            "bmp" => ".bmp",
            "gif" => ".gif",
            "tif" or "tiff" => ".tif",
            _ => string.Empty,
        };
    }

    private static string GetPathWithPreferredExtension(string path, string format)
    {
        var preferredExtension = GetPreferredExtensionByFormat(format);
        if (string.IsNullOrWhiteSpace(preferredExtension))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(directory, fileNameWithoutExtension + preferredExtension);
    }

    private void AlignOutputPathExtensionWithSelectedFormat(string selectedFormatOverride)
    {
        var outputPath = OutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var alignedPath = GetPathWithPreferredExtension(outputPath, selectedFormatOverride);
        if (string.Equals(outputPath, alignedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isUpdatingControls = true;
        OutputPathTextBox.Text = alignedPath;
        _isUpdatingControls = false;
    }

    private void AlignOutputPathExtensionWithInputFormat()
    {
        var outputPath = OutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var inputPath = InputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        var inputExtension = Path.GetExtension(inputPath);
        if (string.IsNullOrWhiteSpace(inputExtension))
        {
            return;
        }

        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputPath);
        var alignedPath = Path.Combine(directory, fileNameWithoutExtension + inputExtension);
        if (string.Equals(outputPath, alignedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isUpdatingControls = true;
        OutputPathTextBox.Text = alignedPath;
        _isUpdatingControls = false;
    }

    private bool TryBuildOptions(out CompressOptions options, out string message)
    {
        var inputPath = InputPathTextBox.Text.Trim();
        var outputPath = OutputPathTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            options = new CompressOptions();
            message = "请先选择输入文件。";
            return false;
        }

        if (!File.Exists(inputPath))
        {
            options = new CompressOptions();
            message = "输入文件不存在。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            options = new CompressOptions();
            message = "请先选择输出文件。";
            return false;
        }

        if (!TryParsePositiveLong(TargetSizeKbTextBox.Text.Trim(), out var targetKb))
        {
            options = new CompressOptions();
            message = "目标大小必须是大于 0 的整数（KB）。";
            return false;
        }

        if (!TryParsePositiveInt(ScalePercentTextBox.Text.Trim(), out var scalePercent))
        {
            options = new CompressOptions();
            message = "缩放比例必须是大于 0 的整数（%）。";
            return false;
        }

        var selectedFormatOverride = GetSelectedOutputFormatOverride();

        string? formatOverride = selectedFormatOverride;
        if (string.IsNullOrWhiteSpace(formatOverride))
        {
            var inputExtension = Path.GetExtension(inputPath);
            var outputExtension = Path.GetExtension(outputPath);

            if (!string.Equals(inputExtension, outputExtension, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSupportedFormatExtension(outputExtension))
                {
                    options = new CompressOptions();
                    message = "输出扩展名不支持，请选择 jpg/jpeg/png/webp/bmp/gif/tif/tiff。";
                    return false;
                }

                var confirm = MessageBox.Show(
                    $"你选择了与输入不同的输出格式：{outputExtension}。\n是否按该格式进行转换后压缩？",
                    "格式转换确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                {
                    options = new CompressOptions();
                    message = "已取消转换，请保持相同扩展名或确认转换后重试。";
                    return false;
                }

                formatOverride = NormalizeFormatByExtension(outputExtension);
                Log.Information("User confirmed format conversion in auto mode. InputExt={InputExt}, OutputExt={OutputExt}, OutputFormatOverride={OutputFormatOverride}", inputExtension, outputExtension, formatOverride);
            }
        }

        options = new CompressOptions
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            TargetSizeKb = targetKb,
            ScalePercent = scalePercent,
            OutputFormatOverride = formatOverride,
        };

        message = string.Empty;
        return true;
    }

    private static bool TryParsePositiveLong(string? text, out long value)
    {
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static bool TryParsePositiveInt(string? text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static string? NormalizeFormatByExtension(string extension)
    {
        return extension.TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "jpeg",
            "png" => "png",
            "webp" => "webp",
            "bmp" => "bmp",
            "gif" => "gif",
            "tif" or "tiff" => "tiff",
            _ => null,
        };
    }

    private static bool IsSupportedFormatExtension(string extension)
    {
        return NormalizeFormatByExtension(extension) is not null;
    }

    private void RenderEstimate(CompressEstimateResult estimate)
    {
        EstimateRichTextBox.Document.Blocks.Clear();

        var paragraph = new Paragraph();
        var statusValueText = estimate.IsSuccess ? "成功" : "失败";
        paragraph.Inlines.Add(new Run("预估状态："));
        paragraph.Inlines.Add(new Run(statusValueText)
        {
            Foreground = estimate.IsSuccess ? Brushes.Green : Brushes.Red,
            FontWeight = FontWeights.Bold,
        });
        paragraph.Inlines.Add(new LineBreak());

        var details = BuildEstimateTextWithoutStatus(estimate);
        if (!estimate.IsSuccess && string.IsNullOrWhiteSpace(GetSelectedOutputFormatOverride()))
        {
            details += "提示：当前为“自动（保持原格式）”。可在“输出格式”中选择 JPEG/WEBP 后重新预估。" + Environment.NewLine;
        }

        paragraph.Inlines.Add(new Run(details));

        EstimateRichTextBox.Document.Blocks.Add(paragraph);
    }

    private void RenderEstimatePlain(string text)
    {
        EstimateRichTextBox.Document.Blocks.Clear();
        EstimateRichTextBox.Document.Blocks.Add(new Paragraph(new Run(text)));
    }

    private void RenderResult(string text, bool isFailure)
    {
        ResultRichTextBox.Document.Blocks.Clear();

        var paragraph = new Paragraph();
        if (isFailure)
        {
            paragraph.Inlines.Add(new Run("【失败】")
            {
                Foreground = Brushes.Red,
                FontWeight = FontWeights.Bold,
            });
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(text)
            {
                Foreground = Brushes.Red,
                FontWeight = FontWeights.Bold,
            });
        }
        else
        {
            paragraph.Inlines.Add(new Run(text));
        }

        ResultRichTextBox.Document.Blocks.Add(paragraph);
    }

    private void RenderFinalResult(CompressResult result, long targetKb)
    {
        ResultRichTextBox.Document.Blocks.Clear();

        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run("最终状态："));
        paragraph.Inlines.Add(new Run(result.IsSuccess ? "成功" : "失败")
        {
            Foreground = result.IsSuccess ? Brushes.Green : Brushes.Red,
            FontWeight = FontWeights.Bold,
        });
        paragraph.Inlines.Add(new LineBreak());

        var details = BuildResultTextWithoutStatus(result, targetKb);
        paragraph.Inlines.Add(new Run(details));

        ResultRichTextBox.Document.Blocks.Add(paragraph);
    }

    private static string BuildEstimateTextWithoutStatus(CompressEstimateResult estimate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"预计是否达标：{(estimate.IsTargetReached ? "是" : "否")}");
        sb.AppendLine($"原始大小：{FormatSize(estimate.OriginalSizeBytes)} ({estimate.OriginalSizeBytes} bytes)");
        sb.AppendLine($"目标大小：{FormatSize(estimate.TargetSizeBytes)} ({estimate.TargetSizeBytes} bytes)");

        if (estimate.EstimatedOutputSizeBytes > 0)
        {
            sb.AppendLine($"预计输出大小：{FormatSize(estimate.EstimatedOutputSizeBytes)} ({estimate.EstimatedOutputSizeBytes} bytes)");
        }
        else
        {
            sb.AppendLine("预计输出大小：N/A");
        }

        if (estimate.EstimatedWidth > 0 && estimate.EstimatedHeight > 0)
        {
            sb.AppendLine($"预计输出尺寸：{estimate.EstimatedWidth} x {estimate.EstimatedHeight}");
        }
        else
        {
            sb.AppendLine("预计输出尺寸：N/A");
        }

        sb.AppendLine($"建议缩放：{estimate.SuggestedScalePercent}%");
        sb.AppendLine($"预计质量参数：{(estimate.EstimatedQuality.HasValue ? estimate.EstimatedQuality.Value.ToString(CultureInfo.InvariantCulture) : "N/A")}");

        if (!string.IsNullOrWhiteSpace(estimate.Message))
        {
            sb.AppendLine($"说明：{estimate.Message}");
        }

        return sb.ToString();
    }

    private static string BuildResultTextWithoutStatus(CompressResult result, long targetKb)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"是否达标(<= {targetKb}KB)：{(result.IsTargetReached ? "是" : "否")}");
        sb.AppendLine($"输入文件：{result.InputPath}");
        sb.AppendLine($"输出文件：{result.OutputPath}");
        sb.AppendLine($"输出格式：{result.OutputFormat}");
        sb.AppendLine($"原始大小：{FormatSize(result.OriginalSizeBytes)} ({result.OriginalSizeBytes} bytes)");
        sb.AppendLine($"输出大小：{FormatSize(result.OutputSizeBytes)} ({result.OutputSizeBytes} bytes)");
        sb.AppendLine($"质量参数：{(result.AppliedQuality.HasValue ? result.AppliedQuality.Value.ToString(CultureInfo.InvariantCulture) : "N/A")}");
        sb.AppendLine($"耗时：{result.Elapsed.TotalMilliseconds:F0} ms");

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            sb.AppendLine($"说明：{result.Message}");
        }

        return sb.ToString();
    }

    private static string FormatSize(long bytes)
    {
        const double kb = 1024d;
        const double mb = 1024d * 1024d;

        if (bytes >= mb)
        {
            return $"{bytes / mb:F2} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:F2} KB";
        }

        return $"{bytes} B";
    }

    private static void LogResult(CompressOptions options, CompressResult result, TimeSpan elapsed)
    {
        if (result.IsSuccess)
        {
            Log.Information(
                "Compression finished. Input={InputPath}, Output={OutputPath}, OriginalBytes={OriginalBytes}, TargetKb={TargetKb}, ScalePercent={ScalePercent}, OutputFormatOverride={OutputFormatOverride}, OutputBytes={OutputBytes}, Reached={Reached}, AppliedQuality={AppliedQuality}, ElapsedMs={ElapsedMs}",
                options.InputPath,
                options.OutputPath,
                result.OriginalSizeBytes,
                options.TargetSizeKb,
                options.ScalePercent,
                options.OutputFormatOverride ?? "auto",
                result.OutputSizeBytes,
                result.IsTargetReached,
                result.AppliedQuality,
                elapsed.TotalMilliseconds);

            return;
        }

        Log.Warning(
            "Compression finished with failure. Input={InputPath}, Output={OutputPath}, OriginalBytes={OriginalBytes}, TargetKb={TargetKb}, ScalePercent={ScalePercent}, OutputFormatOverride={OutputFormatOverride}, OutputBytes={OutputBytes}, Reached={Reached}, AppliedQuality={AppliedQuality}, ElapsedMs={ElapsedMs}, Message={Message}",
            options.InputPath,
            options.OutputPath,
            result.OriginalSizeBytes,
            options.TargetSizeKb,
            options.ScalePercent,
            options.OutputFormatOverride ?? "auto",
            result.OutputSizeBytes,
            result.IsTargetReached,
            result.AppliedQuality,
            elapsed.TotalMilliseconds,
            result.Message);
    }
}
