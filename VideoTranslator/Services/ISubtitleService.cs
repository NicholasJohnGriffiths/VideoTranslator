using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ISubtitleService
{
    Task<string> GenerateWebVttAsync(
        IReadOnlyList<VideoSegment> segments, string outputPath, CancellationToken cancellationToken);
}
