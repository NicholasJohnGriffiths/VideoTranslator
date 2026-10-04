using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed record VoicePreview(int Sequence, string Key, TimeSpan Duration, bool Reused);

public sealed class VoicePreviewService(
    ISpeechSynthesisService speech, IVoicePreviewStorageService storage,
    ILogger<VoicePreviewService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<VoicePreview> GenerateAsync(
        VideoJob job, VideoSegment segment, string text, LanguageOption language, CancellationToken cancellationToken)
    {
        if (job.Status != JobStatus.AwaitingScriptReview || language.Code != job.SelectedLanguage)
        {
            throw new InvalidOperationException("Voice previews require a matching language and a job awaiting script review.");
        }
        return await GenerateCoreAsync(job, segment, text, language, cancellationToken);
    }

    public Task<VoicePreview> GenerateApprovedAsync(VideoJob job, VideoSegment segment, CancellationToken cancellationToken)
    {
        var script = job.ApprovedSpeech;
        if (job.Status != JobStatus.GeneratingSpeech || script is null
            || !script.Segments.Contains(segment))
        {
            throw new InvalidOperationException("Speech generation requires an approved script segment.");
        }
        SpeechGenerationMetadata.Validate(script, job.SelectedLanguage);
        return GenerateCoreAsync(job, segment, segment.TranslatedText, script.Language, cancellationToken);
    }

    private async Task<VoicePreview> GenerateCoreAsync(
        VideoJob job, VideoSegment segment, string text, LanguageOption language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8000)
        {
            throw new SpeechSynthesisException("A blank segment has no voice to preview. Enter text (at most 8000 characters) or reset it to the original translation.");
        }
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            throw new VoicePreviewBusyException();
        }
        string? directory = null;
        try
        {
            var key = VoicePreviewKey.Create(text, language);
            var cached = await storage.GetAsync(job.JobId, segment.Sequence, key, cancellationToken);
            if (cached is not null)
            {
                logger.LogInformation("[Job: {JobId}] Reusing cached voice preview for segment {Sequence}.", job.JobId, segment.Sequence);
                return new(segment.Sequence, key, SpeechWaveAudio.Validate(cached), Reused: true);
            }
            directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-preview-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "preview.wav");
            logger.LogInformation("[Job: {JobId}] Synthesizing preview for segment {Sequence} with {Voice}.",
                job.JobId, segment.Sequence, language.VoiceName);
            await speech.GenerateSpeechAsync(text, language, path, cancellationToken);
            await using var input = File.OpenRead(path);
            var bytes = await SpeechWaveAudio.ReadBoundedAsync(input, cancellationToken);
            var duration = SpeechWaveAudio.Validate(bytes);
            await storage.SaveAsync(job.JobId, segment.Sequence, key, bytes, cancellationToken);
            return new(segment.Sequence, key, duration, Reused: false);
        }
        finally
        {
            if (directory is not null)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException exception) { logger.LogError(exception, "Could not remove temporary preview audio."); }
                catch (UnauthorizedAccessException exception) { logger.LogError(exception, "Access denied removing temporary preview audio."); }
            }
            gate.Release();
        }
    }
}
