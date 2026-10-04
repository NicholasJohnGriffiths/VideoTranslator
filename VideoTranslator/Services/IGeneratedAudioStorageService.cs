using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface IGeneratedAudioStorageService
{
    Task<TimedAudioResult?> GetResultAsync(string jobId, string requestId, CancellationToken cancellationToken);
    Task SaveAsync(string jobId, TimedAudioResult result, string audioPath, CancellationToken cancellationToken);
    Task CopyAudioAsync(string jobId, string requestId, Stream destination, CancellationToken cancellationToken);
}
