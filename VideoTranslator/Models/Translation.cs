namespace VideoTranslator.Models;

public sealed class Translation
{
    public string TargetLanguage { get; init; } = string.Empty;
    public List<VideoSegment> Segments { get; init; } = [];
}
