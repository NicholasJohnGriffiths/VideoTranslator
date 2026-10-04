using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ITranscriptStorageService
{
    Task DownloadAudioAsync(string jobId, string destinationPath, CancellationToken cancellationToken);
    Task SaveTranscriptAsync(string jobId, Transcript transcript, CancellationToken cancellationToken);
    Task<Transcript?> GetTranscriptAsync(string jobId, CancellationToken cancellationToken);
}
