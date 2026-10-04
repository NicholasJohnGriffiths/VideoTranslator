using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed record ScriptReview(Translation Translation, ScriptEditSnapshot Snapshot);

public sealed class ScriptEditingService(ITranslationStorageService storage, ITranscriptStorageService transcripts)
{
    public async Task<ScriptReview?> GetAsync(VideoJob job, CancellationToken cancellationToken)
    {
        var translation = await storage.GetTranslationAsync(job.JobId, cancellationToken);
        if (translation is null)
        {
            return null;
        }
        var transcript = await transcripts.GetTranscriptAsync(job.JobId, cancellationToken)
            ?? throw new InvalidDataException("Original transcript is missing.");
        TranslationMetadata.ValidateAgainstTranscript(translation, transcript, job.SelectedLanguage);
        var edits = await storage.GetEditsAsync(job.JobId, cancellationToken);
        if (edits.Revision is not null)
        {
            TranslationMetadata.ValidateEdits(translation, edits.Edits);
            foreach (var segment in translation.Segments)
            {
                segment.EditedText = edits.Edits.Segments[segment.Sequence];
            }
        }
        return new(translation, edits);
    }

    public async Task SaveAsync(VideoJob job, ScriptEdits edits, string? revision, CancellationToken cancellationToken)
    {
        if (job.Status != JobStatus.AwaitingScriptReview)
        {
            throw new InvalidOperationException("Scripts can be edited only while awaiting review.");
        }
        var review = await GetAsync(job, cancellationToken)
            ?? throw new InvalidDataException("Translation is missing.");
        TranslationMetadata.ValidateEdits(review.Translation, edits);
        await storage.SaveEditsAsync(job.JobId, edits, revision, cancellationToken);
    }
}
