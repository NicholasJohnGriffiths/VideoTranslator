using Microsoft.Extensions.Options;
using VideoTranslator.Models;

namespace VideoTranslator.Configuration;

public sealed class LanguageOptions
{
    public const string SectionName = "Languages";
    public List<LanguageOption> Supported { get; init; } = [];
}

public sealed class LanguageOptionsValidator : IValidateOptions<LanguageOptions>
{
    public ValidateOptionsResult Validate(string? name, LanguageOptions options)
    {
        if (options.Supported.Count == 0
            || options.Supported.Any(language =>
                string.IsNullOrWhiteSpace(language.Code)
                || string.IsNullOrWhiteSpace(language.DisplayName)
                || string.IsNullOrWhiteSpace(language.SpeechLocale)
                || string.IsNullOrWhiteSpace(language.VoiceName))
            || options.Supported.Select(language => language.Code)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Supported.Count)
        {
            return ValidateOptionsResult.Fail(
                "Configure at least one language with a unique code, display name, speech locale and voice.");
        }

        return ValidateOptionsResult.Success;
    }
}
