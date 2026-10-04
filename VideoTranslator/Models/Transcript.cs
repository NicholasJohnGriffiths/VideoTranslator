namespace VideoTranslator.Models;

public sealed class Transcript
{
    public string SourceLanguage { get; init; } = "en";
    public List<VideoSegment> Segments { get; init; } = [];
}
