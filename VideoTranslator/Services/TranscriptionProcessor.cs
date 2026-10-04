using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class TranscriptionProcessor(
    IJobStorageService jobs, ITranscriptStorageService transcripts,
    ITranscriptionService speech, ILogger<TranscriptionProcessor> logger)
{
    public async Task ProcessAsync(VideoJob job, CancellationToken cancellationToken)
    {
        if (job.Status is not (JobStatus.AudioReady or JobStatus.Transcribing))
        {
            throw new InvalidOperationException("Only audio-ready or interrupted transcription jobs can be processed.");
        }
        var directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-transcribe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            if (job.Status == JobStatus.AudioReady)
            {
                job.TransitionTo(JobStatus.Transcribing);
                await jobs.UpdateAsync(job, JobStatus.AudioReady, cancellationToken);
            }
            logger.LogInformation("[Job: {JobId}] Starting English transcription.", job.JobId);
            var transcript = await transcripts.GetTranscriptAsync(job.JobId, cancellationToken);
            if (transcript is null)
            {
                var audioPath = Path.Combine(directory, "original-audio.wav");
                await transcripts.DownloadAudioAsync(job.JobId, audioPath, cancellationToken);
                transcript = await speech.TranscribeAsync(audioPath, cancellationToken);
                await transcripts.SaveTranscriptAsync(job.JobId, transcript, cancellationToken);
            }
            job.TransitionTo(JobStatus.TranscriptReady);
            await jobs.UpdateAsync(job, JobStatus.Transcribing, cancellationToken);
            logger.LogInformation("[Job: {JobId}] English transcript saved with {SegmentCount} segments.",
                job.JobId, transcript.Segments.Count);
        }
        catch (TranscriptionException exception)
        {
            await FailAsync(job, exception.Message, exception, cancellationToken);
        }
        catch (IOException exception)
        {
            await FailAsync(job, "Transcription could not access its audio files. Please upload again.",
                exception, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            await FailAsync(job, "Transcription could not access its audio files. Contact the administrator.",
                exception, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException exception)
            {
                logger.LogError(exception, "[Job: {JobId}] Could not clean transcription directory.", job.JobId);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogError(exception, "[Job: {JobId}] Access denied cleaning transcription directory.", job.JobId);
            }
        }
    }

    private async Task FailAsync(VideoJob job, string message, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "[Job: {JobId}] English transcription failed.", job.JobId);
        job.TransitionTo(JobStatus.Failed, message);
        await jobs.UpdateAsync(job, JobStatus.Transcribing, cancellationToken);
    }
}
