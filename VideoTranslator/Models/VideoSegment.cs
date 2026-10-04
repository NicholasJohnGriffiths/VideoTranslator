namespace VideoTranslator.Models;

public sealed class VideoSegment
{
    public int Sequence { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string OriginalText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public string? EditedText { get; set; }
    public string EffectiveText => EditedText ?? TranslatedText;
}
