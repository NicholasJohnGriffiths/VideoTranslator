using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class VideoService(
    ILanguageService languages, IUploadValidator validator,
    IJobStorageService storage, ILogger<VideoService> logger) : IVideoService
{
    public async Task<VideoJob> UploadAsync(
        Stream content, string fileName, string contentType, long length,
        string targetLanguage, CancellationToken cancellationToken)
    {
        var displayName = validator.ValidateMetadata(fileName, contentType, length);
        if (languages.Find(targetLanguage) is null)
        {
            throw new UploadValidationException("Select one of the configured target languages.");
        }

        var job = new VideoJob
        {
            OriginalFileName = displayName,
            SelectedLanguage = targetLanguage,
            FileSizeBytes = length
        };
        await storage.CreateAsync(job, content, cancellationToken);
        logger.LogInformation("[Job: {JobId}] Video uploaded for {Language}; queued for audio extraction.",
            job.JobId, job.SelectedLanguage);
        return job;
    }
}
