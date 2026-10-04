namespace VideoTranslator.Models;

public enum JobStatus
{
    Uploaded,
    ExtractingAudio,
    Transcribing,
    Translating,
    AwaitingScriptReview,
    GeneratingSpeech,
    CreatingSubtitles,
    RenderingVideo,
    Completed,
    Failed,
    AudioReady,
    TranscriptReady,
    TranslatedAudioReady
}
