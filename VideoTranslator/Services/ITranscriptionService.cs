using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ITranscriptionService
{
    Task<Transcript> TranscribeAsync(string audioPath, CancellationToken cancellationToken);
    Task<Transcript> TranscribeForJobAsync(string jobId, string audioPath, CancellationToken cancellationToken) =>
        TranscribeAsync(audioPath, cancellationToken);
}
