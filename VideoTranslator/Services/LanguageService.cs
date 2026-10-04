using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class LanguageService(IOptions<LanguageOptions> options) : ILanguageService
{
    private readonly IReadOnlyList<LanguageOption> languages = options.Value.Supported.AsReadOnly();

    public IReadOnlyList<LanguageOption> GetLanguages() => languages;

    public LanguageOption? Find(string code) =>
        languages.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.Ordinal));
}
