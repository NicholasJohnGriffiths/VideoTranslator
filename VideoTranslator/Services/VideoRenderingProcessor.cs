using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class VideoRenderingProcessor(
    IJobStorageService jobs, IGeneratedAudioStorageService audio, IRenderedVideoStorageService storage,
    ISubtitleService subtitles, IVideoRenderingService rendering, FFmpegVideoRenderingService verifier,
    ILogger<VideoRenderingProcessor> logger)
{
    public async Task ProcessAsync(VideoJob job, CancellationToken token)
    {
        if (job.Status is not (JobStatus.CreatingSubtitles or JobStatus.RenderingVideo) || job.ApprovedSpeech is null)
        { throw new InvalidDataException("Rendering requires an approved script and queued rendering status."); }
        var script = job.ApprovedSpeech;
        SpeechGenerationMetadata.Validate(script, job.SelectedLanguage);
        var directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var audioResult = await audio.GetResultAsync(job.JobId, script.RequestId, token)
                ?? throw new InvalidDataException("Approved audio timing metadata is missing.");
            SpeechGenerationMetadata.ValidateResult(audioResult, script);
            var vtt = Path.Combine(directory, "subtitles.vtt");
            await subtitles.GenerateWebVttAsync(script.Segments, vtt, token);
            var scriptPath = Path.Combine(directory, "approved-script.json");
            await File.WriteAllBytesAsync(scriptPath, JsonSerializer.SerializeToUtf8Bytes(script, JobMetadata.JsonOptions), token);
            var result = await storage.GetResultAsync(job.JobId, script.RequestId, token);
            if (result is not null)
            {
                foreach (var artifact in Enum.GetValues<RenderArtifact>())
                {
                    var path = Path.Combine(directory, $"saved-{RenderedVideoMetadata.Name(artifact)}");
                    await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
                    await storage.CopyAsync(job.JobId, script.RequestId, artifact, file, token);
                    await RenderedVideoMetadata.VerifyAsync(file, result.Files[artifact], token);
                }
                if (await RenderedVideoMetadata.InspectAsync(vtt, RenderArtifact.Subtitles, token) != result.Files[RenderArtifact.Subtitles]
                    || await RenderedVideoMetadata.InspectAsync(scriptPath, RenderArtifact.Script, token) != result.Files[RenderArtifact.Script])
                { throw new InvalidDataException("Committed output no longer matches its frozen script."); }
                var duration = await verifier.VerifyAsync(Path.Combine(directory, "saved-translated-video.mp4"), audioResult.DurationSeconds, token);
                if (Math.Abs(duration - result.DurationSeconds) > 0.001)
                { throw new InvalidDataException("Committed video duration differs from its manifest."); }
                logger.LogInformation("[Job: {JobId}] Recovered committed video without rendering again.", job.JobId);
            }
            else
            {
                await storage.SaveArtifactAsync(job.JobId, script.RequestId, RenderArtifact.Subtitles, vtt, token);
                await storage.SaveArtifactAsync(job.JobId, script.RequestId, RenderArtifact.Script, scriptPath, token);
            }
            if (job.Status == JobStatus.CreatingSubtitles)
            {
                job.TransitionTo(JobStatus.RenderingVideo);
                await jobs.UpdateAsync(job, JobStatus.CreatingSubtitles, token);
            }
            if (result is null)
            {
                var source = Path.Combine(directory, "original.mp4");
                var wav = Path.Combine(directory, "translated-audio.wav");
                await jobs.DownloadVideoAsync(job, source, token);
                await using (var file = File.Create(wav))
                { await audio.CopyAudioAsync(job.JobId, script.RequestId, file, token); }
                var video = await rendering.RenderTranslatedVideoAsync(source, wav, vtt, token);
                var duration = await verifier.VerifyAsync(video, audioResult.DurationSeconds, token);
                var files = new Dictionary<RenderArtifact, RenderedFile>();
                foreach (var (artifact, path) in new[] { (RenderArtifact.Video, video), (RenderArtifact.Subtitles, vtt), (RenderArtifact.Script, scriptPath) })
                { files.Add(artifact, await RenderedVideoMetadata.InspectAsync(path, artifact, token)); }
                result = new() { RequestId = script.RequestId, DurationSeconds = duration, Files = files };
                await storage.SaveArtifactAsync(job.JobId, script.RequestId, RenderArtifact.Video, video, token);
                await storage.CommitAsync(job.JobId, result, token);
            }
            job.TransitionTo(JobStatus.Completed);
            await jobs.UpdateAsync(job, JobStatus.RenderingVideo, token);
            logger.LogInformation("[Job: {JobId}] Video, WebVTT and approved script committed.", job.JobId);
        }
        catch (Exception exception) when (exception is MediaProcessingException or UploadValidationException)
        {
            logger.LogError(exception, "[Job: {JobId}] Video rendering failed.", job.JobId);
            var expected = job.Status;
            job.TransitionTo(JobStatus.Failed, "Final video rendering failed. Contact the administrator before trying again.");
            await jobs.UpdateAsync(job, expected, token);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException exception) { logger.LogError(exception, "Could not clean rendering folder."); }
            catch (UnauthorizedAccessException exception) { logger.LogError(exception, "Access denied cleaning rendering folder."); }
        }
    }
}
