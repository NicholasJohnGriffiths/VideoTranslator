using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class AudioExtractionWorker(
    IJobStorageService storage, AudioExtractionProcessor processor,
    IOptions<MediaOptions> options, ILogger<AudioExtractionWorker> logger,
    TranscriptionProcessor? transcription = null, TranslationProcessor? translation = null,
    SpeechGenerationProcessor? speechGeneration = null, VideoRenderingProcessor? rendering = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Persistent job records are the work queue for this single-instance phase.
                await foreach (var id in storage.ListJobIdsAsync(stoppingToken))
                {
                    using var scope = logger.BeginScope("[Job: {JobId}]", id);
                    try
                    {
                        var job = await storage.GetAsync(id, stoppingToken);
                        if (job?.Status is JobStatus.Uploaded or JobStatus.ExtractingAudio)
                        {
                            await processor.ProcessAsync(job, stoppingToken);
                        }
                        if (transcription is not null && job?.Status is JobStatus.AudioReady or JobStatus.Transcribing)
                        {
                            await transcription.ProcessAsync(job, stoppingToken);
                        }
                        if (translation is not null && job?.Status is JobStatus.TranscriptReady or JobStatus.Translating)
                        {
                            await translation.ProcessAsync(job, stoppingToken);
                        }
                        if (speechGeneration is not null && job?.Status == JobStatus.GeneratingSpeech)
                        {
                            await speechGeneration.ProcessAsync(job, stoppingToken);
                        }
                        if (rendering is not null && job?.Status is JobStatus.CreatingSubtitles or JobStatus.RenderingVideo)
                        {
                            await rendering.ProcessAsync(job, stoppingToken);
                        }
                    }
                    catch (VoicePreviewBusyException exception)
                    {
                        logger.LogInformation("Speech generation will retry next scan: {Message}", exception.Message);
                    }
                    catch (JobStorageException exception)
                    {
                        logger.LogError(exception, "Storage failed; pending processing will be retried.");
                    }
                    catch (IOException exception)
                    {
                        logger.LogError(exception, "Job storage file could not be read.");
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        logger.LogError(exception, "Job storage file access denied.");
                    }
                    catch (JsonException exception)
                    {
                        logger.LogError(exception, "Job metadata is corrupt; repair or remove this job.");
                    }
                    catch (InvalidDataException exception)
                    {
                        logger.LogError(exception, "Job metadata is inconsistent; repair or remove this job.");
                    }
                }
            }
            catch (JobStorageException exception)
            {
                logger.LogError(exception, "Could not enumerate pending jobs; retrying next scan.");
            }
            catch (IOException exception)
            {
                logger.LogError(exception, "Could not enumerate local job files; retrying next scan.");
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogError(exception, "Access denied enumerating local job files; retrying next scan.");
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
        }
    }
}
