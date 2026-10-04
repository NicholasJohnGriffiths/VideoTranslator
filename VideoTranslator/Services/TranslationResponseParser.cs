using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public static class TranslationResponseParser
{
    public static IReadOnlyList<VideoSegment> Parse(string json, IReadOnlyList<VideoSegment> source)
    {
        try
        {
            using var response = JsonDocument.Parse(json);
            var root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() != 1 || choices[0].ValueKind != JsonValueKind.Object
                || !choices[0].TryGetProperty("finish_reason", out var finish) || finish.GetString() != "stop"
                || !choices[0].TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null)
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            {
                throw new TranslationException("OpenAI did not return a complete translation. The response may have been refused or truncated.");
            }
            using var structured = JsonDocument.Parse(content.GetString()!);
            if (structured.RootElement.ValueKind != JsonValueKind.Object
                || !structured.RootElement.TryGetProperty("segments", out var segments)
                || segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() != source.Count)
            {
                throw new TranslationException("OpenAI returned an incorrect number of translated segments.");
            }
            var translated = new Dictionary<int, string>();
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object
                    || !segment.TryGetProperty("sequence", out var sequence) || sequence.ValueKind != JsonValueKind.Number
                    || !sequence.TryGetInt32(out var id)
                    || !segment.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(text.GetString()) || text.GetString()!.Length > 8000
                    || !translated.TryAdd(id, text.GetString()!))
                {
                    throw new TranslationException("OpenAI returned invalid or duplicate segment translations.");
                }
            }
            return source.Select(segment =>
            {
                if (!translated.TryGetValue(segment.Sequence, out var text))
                {
                    throw new TranslationException("OpenAI changed the transcript's segment identifiers.");
                }
                return new VideoSegment
                {
                    Sequence = segment.Sequence, Start = segment.Start, End = segment.End,
                    OriginalText = segment.OriginalText, TranslatedText = text
                };
            }).ToArray();
        }
        catch (JsonException exception)
        {
            throw new TranslationException("OpenAI returned unreadable structured translation data.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new TranslationException("OpenAI returned invalid translation field types.", exception);
        }
    }
}
