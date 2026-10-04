using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public static class SpeechTranscriptionParser
{
    public static Transcript Parse(string json, string sourceLocale)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("durationMilliseconds", out var durationValue)
                || !Milliseconds(durationValue, out var duration) || duration <= 0
                || !root.TryGetProperty("phrases", out var phrases) || phrases.ValueKind != JsonValueKind.Array)
            {
                throw new TranscriptionException("Speech returned an invalid time-coded transcript.");
            }
            var segments = new List<VideoSegment>();
            foreach (var phrase in phrases.EnumerateArray())
            {
                if (phrase.ValueKind != JsonValueKind.Object
                    || !phrase.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(text.GetString())
                    || !phrase.TryGetProperty("offsetMilliseconds", out var offsetValue)
                    || !Milliseconds(offsetValue, out var offset) || offset < 0
                    || !phrase.TryGetProperty("durationMilliseconds", out var lengthValue)
                    || !Milliseconds(lengthValue, out var length) || length <= 0
                    || offset > duration || length > duration - offset)
                {
                    throw new TranscriptionException("Speech returned a segment with invalid text or timestamps.");
                }
                segments.Add(new VideoSegment
                {
                    Start = TimeSpan.FromMilliseconds(offset),
                    End = TimeSpan.FromMilliseconds(offset + length),
                    OriginalText = text.GetString()!
                });
            }
            if (segments.Count == 0)
            {
                throw new TranscriptionException("No English speech was recognised. Check that the video contains clear English speech.");
            }
            var ordered = segments.OrderBy(segment => segment.Start).ThenBy(segment => segment.End).ToList();
            return new Transcript
            {
                SourceLanguage = sourceLocale,
                Segments = ordered.Select((segment, index) => new VideoSegment
                {
                    Sequence = index + 1, Start = segment.Start, End = segment.End,
                    OriginalText = segment.OriginalText
                }).ToList()
            };
        }
        catch (JsonException exception)
        {
            throw new TranscriptionException("Speech returned an unreadable transcript.", exception);
        }
    }

    private static bool Milliseconds(JsonElement value, out long milliseconds)
    {
        milliseconds = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out milliseconds)
            && milliseconds <= TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
    }
}
