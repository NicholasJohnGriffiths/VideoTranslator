using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ITranslationService
{
    Task<Translation> TranslateAsync(
        Transcript transcript, LanguageOption targetLanguage, CancellationToken cancellationToken);
}
