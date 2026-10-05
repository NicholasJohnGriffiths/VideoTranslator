namespace VideoTranslator.Models;

public sealed record JobNextStep(string Instruction, string? Page = null, string? Action = null)
{
    public static JobNextStep For(VideoJob job, bool transcriptionEnabled, bool translationEnabled,
        bool synthesisEnabled, bool processingEnabled)
    {
        if (job.Status == JobStatus.Failed)
            return new("Processing stopped. Check the error below and contact the administrator with your job ID.");
        if (job.Status == JobStatus.Completed)
            return new("Your video is ready. Watch it with subtitles or download the finished translation.", "/Result", "Watch translated video");
        if (job.Status == JobStatus.AwaitingScriptReview)
            return synthesisEnabled
                ? new(job.ReviewMessage is null
                    ? "Review the translated wording, save any edits, then approve it to generate the voice track."
                    : "Check the timing warnings in script review. Save any wording changes, then approve audio generation again.",
                    "/Script", "Review script & generate audio")
                : new("You can review and save the script, but audio generation is disabled. Contact the administrator to continue.",
                    "/Script", "Review script");
        if (job.Status == JobStatus.TranslatedAudioReady)
            return new("Listen to the translated audio. If it sounds right, confirm replacement of the original audio and choose Render final video.",
                "/TranslatedAudio", "Listen & approve video rendering");
        if (!processingEnabled
            || (job.Status is JobStatus.AudioReady or JobStatus.Transcribing && !transcriptionEnabled)
            || (job.Status is JobStatus.TranscriptReady or JobStatus.Translating && !translationEnabled)
            || (job.Status == JobStatus.GeneratingSpeech && !synthesisEnabled))
            return new("Processing is paused in configuration. No approval is needed here; contact the administrator to continue.");
        return job.Status switch
        {
            JobStatus.Uploaded or JobStatus.ExtractingAudio =>
                new("Please wait while we check your video and extract its audio. The job page refreshes automatically; nothing to approve yet."),
            JobStatus.AudioReady or JobStatus.Transcribing =>
                new("Please wait while we transcribe the English speech. The job page refreshes automatically; script review comes next."),
            JobStatus.TranscriptReady or JobStatus.Translating =>
                new("Please wait while we translate the transcript. The job page refreshes automatically; you will review the script next."),
            JobStatus.GeneratingSpeech =>
                new("Please wait while we generate and check the voice track. The job page refreshes automatically; listen and approve rendering when it is ready."),
            JobStatus.CreatingSubtitles or JobStatus.RenderingVideo =>
                new("Please wait while we create subtitles and the final video. The job page refreshes automatically; no further approval is needed."),
            _ => throw new InvalidDataException("Unknown job status for next-step guidance.")
        };
    }
}
