using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ILanguageService
{
    IReadOnlyList<LanguageOption> GetLanguages();
    LanguageOption? Find(string code);
}
