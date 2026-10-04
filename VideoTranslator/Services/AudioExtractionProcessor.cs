using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class AudioExtractionProcessor(
    IJobStorageService storage, IAudioService audio, ILogger<AudioExtractionProcessor> logger)
{
    public async Task ProcessAsync(VideoJob job, CancellationToken cancellationToken)
    {
        if (job.Status is not (JobStatus.Uploaded or JobStatus.ExtractingAudio))
        {
            throw new InvalidOperationException("Only pending audio extraction jobs can be processed.");
        }

        var workingDirectory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-job-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            if (job.Status == JobStatus.Uploaded)
            {
                job.TransitionTo(JobStatus.ExtractingAudio);
                await storage.UpdateAsync(job, JobStatus.Uploaded, cancellationToken);
            }
            logger.LogInformation("[Job: {JobId}] Validating media and extracting audio.", job.JobId);
            var videoPath = Path.Combine(workingDirectory, "original-video.mp4");
            await storage.DownloadVideoAsync(job, videoPath, cancellationToken);
            var audioPath = await audio.ExtractAudioAsync(videoPath, workingDirectory, cancellationToken);
            await storage.SaveAudioAsync(job.JobId, audioPath, cancellationToken);
            job.TransitionTo(JobStatus.AudioReady);
            await storage.UpdateAsync(job, JobStatus.ExtractingAudio, cancellationToken);
            logger.LogInformation("[Job: {JobId}] Audio extracted and saved.", job.JobId);
        }
        catch (MediaProcessingException exception)
        {
            await FailAsync(job, exception.Message, exception, cancellationToken);
        }
        catch (UploadValidationException exception)
        {
            await FailAsync(job, "The stored upload is incomplete or invalid. Please upload the video again.",
                exception, cancellationToken);
        }
        catch (IOException exception)
        {
            await FailAsync(job, "Audio extraction could not access its working files. Please upload again.",
                exception, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            await FailAsync(job, "Audio extraction could not access its working files. Contact the administrator.",
                exception, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException exception)
            {
                logger.LogError(exception, "[Job: {JobId}] Could not clean processing directory.", job.JobId);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogError(exception, "[Job: {JobId}] Access denied cleaning processing directory.", job.JobId);
            }
        }
    }

    private async Task FailAsync(VideoJob job, string message, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "[Job: {JobId}] Audio extraction failed.", job.JobId);
        job.TransitionTo(JobStatus.Failed, message);
        await storage.UpdateAsync(job, JobStatus.ExtractingAudio, cancellationToken);
    }
}
