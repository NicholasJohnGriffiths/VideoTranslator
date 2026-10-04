namespace VideoTranslator.Models;

public sealed class SpeechGeneration
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public LanguageOption Language { get; init; } = new();
    public List<VideoSegment> Segments { get; init; } = [];
    public string? ScriptRevision { get; init; }
    public double MaximumSpeed { get; init; } = 1.25;
}

public sealed class TimedAudioResult
{
    public string RequestId { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
    public List<SegmentAudioTiming> Segments { get; init; } = [];
}

public sealed record SegmentAudioTiming(int Sequence, double SourceSeconds, double SlotSeconds, double Speed);
