namespace VideoTranslator.Models;

public sealed class LanguageOption
{
    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SpeechLocale { get; init; } = string.Empty;
    public string VoiceName { get; init; } = string.Empty;
    public bool IsRightToLeft { get; init; }
}
