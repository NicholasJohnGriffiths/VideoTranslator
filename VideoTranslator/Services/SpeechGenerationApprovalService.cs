using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class SpeechGenerationApprovalService(IJobStorageService jobs, ScriptEditingService editor)
{
    public async Task ApproveAsync(VideoJob job, LanguageOption language, string? revision,
        IReadOnlyList<string> submittedText, CancellationToken cancellationToken)
    {
        if (job.Status != JobStatus.AwaitingScriptReview)
        {
            throw new InvalidOperationException("This job is no longer awaiting script review.");
        }
        var saved = await editor.GetAsync(job, cancellationToken)
            ?? throw new InvalidDataException("Saved translation is missing.");
        if (saved.Snapshot.Revision != revision)
        {
            throw new ScriptConflictException();
        }
        if (!submittedText.SequenceEqual(saved.Translation.Segments.Select(segment => segment.EffectiveText)))
        {
            throw new SpeechTimingException("Save your edits before generating full audio. Approval uses the saved script, not unsaved changes.");
        }
        var script = new SpeechGeneration
        {
            Language = language, ScriptRevision = revision,
            Segments = saved.Translation.Segments.Select(segment => new VideoSegment
            {
                Sequence = segment.Sequence, Start = segment.Start, End = segment.End,
                OriginalText = segment.OriginalText, TranslatedText = segment.EffectiveText
            }).ToList()
        };
        SpeechGenerationMetadata.Validate(script, job.SelectedLanguage);
        if (script.Segments.All(segment => string.IsNullOrWhiteSpace(segment.TranslatedText)))
        {
            throw new SpeechTimingException("The entire script is blank. Enter and save spoken text before generating full audio.");
        }
        job.ApproveSpeech(script);
        await jobs.UpdateAsync(job, JobStatus.AwaitingScriptReview, cancellationToken);
    }
}
