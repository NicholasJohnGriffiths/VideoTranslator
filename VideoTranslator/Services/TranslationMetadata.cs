using VideoTranslator.Models;

namespace VideoTranslator.Services;

public static class TranslationMetadata
{
    public static void Validate(Translation translation)
    {
        if (string.IsNullOrWhiteSpace(translation.TargetLanguage) || translation.Segments is null)
        {
            throw new InvalidDataException("Translation language or segments are missing.");
        }
        TranscriptMetadata.Validate(new Transcript { Segments = translation.Segments });
        if (translation.Segments.Any(segment => string.IsNullOrWhiteSpace(segment.TranslatedText)
            || segment.TranslatedText.Length > 8000 || segment.EditedText is not null))
        {
            throw new InvalidDataException("Original translation contains invalid text or embedded edits.");
        }
    }

    public static void ValidateAgainstTranscript(Translation translation, Transcript transcript, string targetLanguage)
    {
        Validate(translation);
        TranscriptMetadata.Validate(transcript);
        if (translation.TargetLanguage != targetLanguage || translation.Segments.Count != transcript.Segments.Count
            || translation.Segments.Zip(transcript.Segments).Any(pair =>
                pair.First.Sequence != pair.Second.Sequence || pair.First.Start != pair.Second.Start
                || pair.First.End != pair.Second.End || pair.First.OriginalText != pair.Second.OriginalText))
        {
            throw new InvalidDataException("Translation does not match the original transcript or requested language.");
        }
    }

    public static void ValidateEdits(Translation translation, ScriptEdits edits)
    {
        if (edits.Segments is null || edits.Segments.Count != translation.Segments.Count
            || translation.Segments.Any(segment => !edits.Segments.ContainsKey(segment.Sequence))
            || edits.Segments.Values.Any(text => text is { Length: > 8000 }))
        {
            throw new InvalidDataException("Edited script must contain every original segment exactly once, with at most 8000 characters each.");
        }
    }
}
