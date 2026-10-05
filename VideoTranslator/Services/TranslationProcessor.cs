using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class TranslationProcessor(
    IJobStorageService jobs, ITranscriptStorageService transcripts,
    ITranslationStorageService translations, ITranslationService translator,
    ILanguageService languages, ILogger<TranslationProcessor> logger)
{
    public async Task ProcessAsync(VideoJob job, CancellationToken cancellationToken)
    {
        if (job.Status is not (JobStatus.TranscriptReady or JobStatus.Translating))
        {
            throw new InvalidOperationException("Only transcript-ready or interrupted translation jobs can be processed.");
        }
        if (job.Status == JobStatus.TranscriptReady)
        {
            job.TransitionTo(JobStatus.Translating);
            await jobs.UpdateAsync(job, JobStatus.TranscriptReady, cancellationToken);
        }
        try
        {
            var transcript = await transcripts.GetTranscriptAsync(job.JobId, cancellationToken)
                ?? throw new InvalidDataException("English transcript is missing.");
            var language = languages.Find(job.SelectedLanguage)
                ?? throw new InvalidDataException("Requested language is no longer configured.");
            var translation = await translations.GetTranslationAsync(job.JobId, cancellationToken);
            if (translation is null)
            {
                logger.LogInformation("[Job: {JobId}] Starting {Language} translation via private OpenAI.", job.JobId, language.Code);
                translation = await translator.TranslateForJobAsync(job.JobId, transcript, language, cancellationToken);
                TranslationMetadata.ValidateAgainstTranscript(translation, transcript, language.Code);
                await translations.SaveTranslationAsync(job.JobId, translation, cancellationToken);
            }
            TranslationMetadata.ValidateAgainstTranscript(translation, transcript, language.Code);
            job.TransitionTo(JobStatus.AwaitingScriptReview);
            await jobs.UpdateAsync(job, JobStatus.Translating, cancellationToken);
            logger.LogInformation("[Job: {JobId}] Translation saved; awaiting script review.", job.JobId);
        }
        catch (TranslationException exception)
        {
            logger.LogError(exception, "[Job: {JobId}] Translation failed.", job.JobId);
            job.TransitionTo(JobStatus.Failed, exception.Message);
            await jobs.UpdateAsync(job, JobStatus.Translating, cancellationToken);
        }
    }
}
