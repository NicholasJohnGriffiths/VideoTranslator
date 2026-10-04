using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class SpeechGenerationProcessor(
    IJobStorageService jobs, VoicePreviewService voices, IVoicePreviewStorageService rawAudio,
    IGeneratedAudioStorageService outputStorage, ISpeechTimingService timing,
    MediaAudioInspector inspector, ILogger<SpeechGenerationProcessor> logger)
{
    public async Task ProcessAsync(VideoJob job, CancellationToken cancellationToken)
    {
        if (job.Status != JobStatus.GeneratingSpeech || job.ApprovedSpeech is null)
        {
            throw new InvalidDataException("Speech generation job has no approved script.");
        }
        var script = job.ApprovedSpeech;
        SpeechGenerationMetadata.Validate(script, job.SelectedLanguage);
        var directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-generate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "translated-audio.wav");
            var result = await outputStorage.GetResultAsync(job.JobId, script.RequestId, cancellationToken);
            if (result is not null)
            {
                SpeechGenerationMetadata.ValidateResult(result, script);
                await using (var destination = File.Create(output))
                {
                    await outputStorage.CopyAudioAsync(job.JobId, script.RequestId, destination, cancellationToken);
                }
                var storedDuration = await inspector.GetPcmDurationAsync(output, cancellationToken);
                if (Math.Abs(storedDuration - result.DurationSeconds) > 0.001)
                {
                    throw new InvalidDataException("Saved audio duration differs from its timing metadata.");
                }
                logger.LogInformation("[Job: {JobId}] Recovered committed translated audio without synthesizing again.", job.JobId);
            }
            else
            {
                var video = Path.Combine(directory, "original.mp4");
                await jobs.DownloadVideoAsync(job, video, cancellationToken);
                var duration = await inspector.GetVideoDurationAsync(video, cancellationToken);
                FFmpegSpeechTimingService.ValidateTimeline(script, duration);
                var paths = new List<string?>();
                foreach (var segment in script.Segments)
                {
                    if (string.IsNullOrWhiteSpace(segment.TranslatedText))
                    {
                        paths.Add(null);
                        continue;
                    }
                    var preview = await voices.GenerateApprovedAsync(job, segment, cancellationToken);
                    var bytes = await rawAudio.GetAsync(job.JobId, segment.Sequence, preview.Key, cancellationToken)
                        ?? throw new InvalidDataException("Generated segment audio is missing.");
                    var path = Path.Combine(directory, $"segment-{segment.Sequence}.wav");
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                    try
                    {
                        FFmpegSpeechTimingService.RequiredSpeed(preview.Duration.TotalSeconds,
                            (segment.End - segment.Start).TotalSeconds, script.MaximumSpeed);
                    }
                    catch (SpeechTimingException exception)
                    {
                        throw new SpeechTimingException($"Segment {segment.Sequence}: {exception.Message}");
                    }
                    paths.Add(path);
                }
                var timings = await timing.SynchronizeAsync(script, paths, duration, output, cancellationToken);
                result = new TimedAudioResult
                {
                    RequestId = script.RequestId,
                    DurationSeconds = await inspector.GetPcmDurationAsync(output, cancellationToken), Segments = timings.ToList()
                };
                SpeechGenerationMetadata.ValidateResult(result, script);
                await outputStorage.SaveAsync(job.JobId, result, output, cancellationToken);
            }
            job.TransitionTo(JobStatus.TranslatedAudioReady);
            await jobs.UpdateAsync(job, JobStatus.GeneratingSpeech, cancellationToken);
            logger.LogInformation("[Job: {JobId}] Full translated audio saved and timing verified.", job.JobId);
        }
        catch (SpeechTimingException exception)
        {
            logger.LogWarning(exception, "[Job: {JobId}] Speech timing requires script review.", job.JobId);
            job.ReturnToScriptReview(exception.Message);
            await jobs.UpdateAsync(job, JobStatus.GeneratingSpeech, cancellationToken);
        }
        catch (Exception exception) when (exception is SpeechSynthesisException or MediaProcessingException or UploadValidationException)
        {
            logger.LogError(exception, "[Job: {JobId}] Speech generation failed.", job.JobId);
            job.TransitionTo(JobStatus.Failed, exception is MediaProcessingException
                ? "Translated audio timing or assembly failed. Contact the administrator before trying again."
                : exception.Message);
            await jobs.UpdateAsync(job, JobStatus.GeneratingSpeech, cancellationToken);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException exception) { logger.LogError(exception, "Could not clean full audio generation folder."); }
            catch (UnauthorizedAccessException exception) { logger.LogError(exception, "Access denied cleaning full audio generation folder."); }
        }
    }
}
