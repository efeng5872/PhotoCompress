using PhotoCompress.Core.Models;

namespace PhotoCompress.Core.Services;

public interface IImageCompressionService
{
    Task<CompressResult> CompressAsync(CompressOptions options, CancellationToken cancellationToken = default);

    Task<CompressEstimateResult> EstimateAsync(CompressOptions options, CancellationToken cancellationToken = default);
}
