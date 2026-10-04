using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ISpeechSynthesisService
{
    Task<string> GenerateSpeechAsync(
        string text, LanguageOption language, string outputPath, CancellationToken cancellationToken);
}
