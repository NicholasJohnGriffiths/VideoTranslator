using VideoTranslator.Models;

namespace VideoTranslator.Services;

public static class SpeechGenerationMetadata
{
    public static void Validate(SpeechGeneration script, string language)
    {
        if (!Guid.TryParseExact(script.RequestId, "N", out _))
        { throw new InvalidDataException("Approved speech request identifier is invalid."); }
        if (script.Language is null || script.Segments is null
            || script.Language.Code != language || string.IsNullOrWhiteSpace(script.Language.SpeechLocale)
            || string.IsNullOrWhiteSpace(script.Language.VoiceName)
            || script.MaximumSpeed < 1 || script.MaximumSpeed > SpeechGeneration.NewApprovalMaximumSpeed
            || !double.IsFinite(script.MaximumSpeed)
            || script.Segments.Any(segment => segment is null || segment.TranslatedText is null
                || segment.EditedText is not null || segment.TranslatedText.Length > 8000))
        {
            throw new InvalidDataException("Approved speech script or voice configuration is invalid.");
        }
        TranscriptMetadata.Validate(new Transcript { Segments = script.Segments });
    }

    public static void ValidateResult(TimedAudioResult result, SpeechGeneration script)
    {
        Validate(script, script.Language?.Code ?? string.Empty);
        if (result.RequestId != script.RequestId || !double.IsFinite(result.DurationSeconds)
            || result.DurationSeconds <= 0 || result.DurationSeconds >= 5 * 60 * 60
            || result.Segments is null || result.Segments.Count != script.Segments.Count
            || result.Segments.Zip(script.Segments).Select((pair, index) => (pair, index)).Any(item =>
            {
                var pair = item.pair;
                return
                pair.First is null || pair.First.Sequence != pair.Second.Sequence || !double.IsFinite(pair.First.SourceSeconds)
                || pair.First.SourceSeconds < 0 || !double.IsFinite(pair.First.Speed)
                || !double.IsFinite(pair.First.SlotSeconds) || pair.First.SlotSeconds <= 0
                || pair.First.Speed < 1 || pair.First.Speed > script.MaximumSpeed
                || Math.Abs(pair.First.SlotSeconds - SpeechTimingWindow.SlotSeconds(script, item.index, result.DurationSeconds)) > 0.001
                || pair.Second.End.TotalSeconds > result.DurationSeconds + 0.001;
            }))
        {
            throw new InvalidDataException("Generated audio timing metadata does not match the approved script.");
        }
    }
}
