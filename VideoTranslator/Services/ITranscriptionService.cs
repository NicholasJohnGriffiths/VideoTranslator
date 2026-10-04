using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ITranscriptionService
{
    Task<Transcript> TranscribeAsync(string audioPath, CancellationToken cancellationToken);
}
