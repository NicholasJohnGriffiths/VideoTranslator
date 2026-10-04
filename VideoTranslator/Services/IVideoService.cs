using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface IVideoService
{
    Task<VideoJob> UploadAsync(
        Stream content, string fileName, string contentType, long length,
        string targetLanguage, CancellationToken cancellationToken);
}
