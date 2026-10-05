using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ITranslationService
{
    Task<Translation> TranslateAsync(
        Transcript transcript, LanguageOption targetLanguage, CancellationToken cancellationToken);
    Task<Translation> TranslateForJobAsync(string jobId, Transcript transcript,
        LanguageOption targetLanguage, CancellationToken cancellationToken) =>
        TranslateAsync(transcript, targetLanguage, cancellationToken);
}
