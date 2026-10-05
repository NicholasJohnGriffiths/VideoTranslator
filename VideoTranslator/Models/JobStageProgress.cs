namespace VideoTranslator.Models;

public enum StageProgressState { Waiting, Running, Complete, Review, Paused, Failed, Stopped }

public sealed record JobStageProgress(string Title, StageProgressState State, string Description)
{
    public int? Value => State == StageProgressState.Running ? null : State == StageProgressState.Complete ? 100 : 0;
    public string CssClass => State.ToString().ToLowerInvariant();

    public static IReadOnlyList<JobStageProgress> For(
        VideoJob job, bool transcriptionEnabled, bool translationEnabled, bool synthesisEnabled)
    {
        string[] titles = ["Upload saved", "Validate video & extract audio", "Transcribe English speech",
            "Translate & review script", "Generate & time translated audio", "Create subtitles", "Render & download video"];
        var failed = job.Status == JobStatus.Failed;
        var status = failed ? job.FailedAtStatus ?? JobStatus.ExtractingAudio : job.Status;
        var current = status switch
        {
            JobStatus.Uploaded or JobStatus.ExtractingAudio => 1,
            JobStatus.AudioReady or JobStatus.Transcribing => 2,
            JobStatus.TranscriptReady or JobStatus.Translating or JobStatus.AwaitingScriptReview => 3,
            JobStatus.GeneratingSpeech => 4,
            JobStatus.TranslatedAudioReady or JobStatus.CreatingSubtitles => 5,
            JobStatus.RenderingVideo => 6,
            JobStatus.Completed => 7,
            _ => throw new InvalidDataException("Unknown job status for stage progress.")
        };
        return titles.Select((title, index) =>
        {
            if (index < current)
                return new JobStageProgress(title, StageProgressState.Complete, "Complete");
            if (index > current)
            {
                if (failed)
                    return new JobStageProgress(title, StageProgressState.Stopped, "Stopped");
                if (index == 4 && job.ReviewMessage is not null && status == JobStatus.AwaitingScriptReview)
                    return new JobStageProgress(title, StageProgressState.Review, "Returned to script review");
                return new JobStageProgress(title, StageProgressState.Waiting, "Waiting");
            }
            if (failed)
                return new JobStageProgress(title, StageProgressState.Failed, "Failed");
            if (status == JobStatus.AwaitingScriptReview)
                return new JobStageProgress(title, StageProgressState.Review, "Ready for review");
            if (status == JobStatus.TranslatedAudioReady)
                return new JobStageProgress(title, StageProgressState.Review, "Awaiting rendering approval");
            if ((index == 2 && !transcriptionEnabled) || (index == 3 && !translationEnabled)
                || (index == 4 && !synthesisEnabled))
                return new JobStageProgress(title, StageProgressState.Paused, "Disabled");
            if (status is JobStatus.Uploaded or JobStatus.AudioReady or JobStatus.TranscriptReady)
                return new JobStageProgress(title, StageProgressState.Waiting, "Queued");
            return new JobStageProgress(title, StageProgressState.Running, "Processing");
        }).ToArray();
    }
}
