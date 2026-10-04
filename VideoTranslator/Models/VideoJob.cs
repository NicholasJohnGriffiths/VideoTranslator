using System.Text.Json.Serialization;

namespace VideoTranslator.Models;

public sealed class VideoJob
{
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");
    public string OriginalFileName { get; init; } = string.Empty;
    public string SelectedLanguage { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    [JsonInclude]
    public JobStatus Status { get; private set; } = JobStatus.Uploaded;
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    [JsonInclude]
    public DateTime? CompletedUtc { get; private set; }
    [JsonInclude]
    public string? ErrorMessage { get; private set; }
    [JsonInclude]
    public JobStatus? FailedAtStatus { get; private set; }
    [JsonInclude]
    public SpeechGeneration? ApprovedSpeech { get; private set; }
    [JsonInclude]
    public string? ReviewMessage { get; private set; }

    public void ApproveSpeech(SpeechGeneration speech)
    {
        if (Status != JobStatus.AwaitingScriptReview)
        {
            throw new InvalidOperationException("Only a script awaiting review can be approved.");
        }
        ApprovedSpeech = speech;
        ReviewMessage = null;
        TransitionTo(JobStatus.GeneratingSpeech);
    }

    public void ReturnToScriptReview(string message)
    {
        if (Status != JobStatus.GeneratingSpeech || string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("Only speech generation can return to review with a useful message.");
        }
        Status = JobStatus.AwaitingScriptReview;
        ReviewMessage = message;
        ApprovedSpeech = null;
    }

    public void TransitionTo(JobStatus status, string? errorMessage = null)
    {
        if (Status is JobStatus.Completed or JobStatus.Failed)
        {
            throw new InvalidOperationException("A finished job cannot change status.");
        }

        if (status == JobStatus.Failed)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                throw new ArgumentException("A failed job needs a useful error message.", nameof(errorMessage));
            }

            FailedAtStatus = Status;
            Status = status;
            ErrorMessage = errorMessage;
            CompletedUtc = DateTime.UtcNow;
            return;
        }

        var next = Status switch
        {
            JobStatus.Uploaded => JobStatus.ExtractingAudio,
            JobStatus.ExtractingAudio => JobStatus.AudioReady,
            JobStatus.AudioReady => JobStatus.Transcribing,
            JobStatus.Transcribing => JobStatus.TranscriptReady,
            JobStatus.TranscriptReady => JobStatus.Translating,
            JobStatus.Translating => JobStatus.AwaitingScriptReview,
            JobStatus.AwaitingScriptReview => JobStatus.GeneratingSpeech,
            JobStatus.GeneratingSpeech => JobStatus.TranslatedAudioReady,
            JobStatus.TranslatedAudioReady => JobStatus.CreatingSubtitles,
            JobStatus.CreatingSubtitles => JobStatus.RenderingVideo,
            JobStatus.RenderingVideo => JobStatus.Completed,
            _ => throw new InvalidOperationException("Unknown job status.")
        };

        if (status != next)
        {
            throw new InvalidOperationException($"Cannot transition from {Status} to {status}.");
        }

        Status = status;
        if (status == JobStatus.Completed)
        {
            CompletedUtc = DateTime.UtcNow;
        }
    }
}
