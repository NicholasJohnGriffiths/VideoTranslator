using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class AzureSpeechTranscriptionService(
    HttpClient client, TokenCredential credential, IOptions<AzureSpeechOptions> options,
    ILogger<AzureSpeechTranscriptionService> logger, JobUsageService? usage = null,
    MediaAudioInspector? inspector = null) : ITranscriptionService
{
    public Task<Transcript> TranscribeAsync(string audioPath, CancellationToken cancellationToken) =>
        TranscribeCoreAsync(null, audioPath, cancellationToken);

    public Task<Transcript> TranscribeForJobAsync(string jobId, string audioPath, CancellationToken cancellationToken) =>
        TranscribeCoreAsync(jobId, audioPath, cancellationToken);

    private async Task<Transcript> TranscribeCoreAsync(string? jobId, string audioPath, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var fileSize = new FileInfo(audioPath).Length;
        // Fast transcription accepts <500 MB and <5 hours; PCM here is mono 16kHz, 16bit.
        if (fileSize <= 44 || fileSize >= 500_000_000 || fileSize >= 5L * 60 * 60 * 32000)
        {
            throw new TranscriptionException("Audio is empty or exceeds fast transcription limits (500 MB / 5 hours). Try a shorter video.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), linked.Token);
            var endpoint = new Uri(new Uri(settings.Endpoint),
                $"speechtotext/transcriptions:transcribe?api-version={settings.ApiVersion}");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var form = new MultipartFormDataContent();
            await using var input = File.OpenRead(audioPath);
            var audio = new StreamContent(input);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "audio", "original-audio.wav");
            var definition = JsonSerializer.Serialize(new { locales = new[] { settings.SourceLocale } });
            form.Add(new StringContent(definition, Encoding.UTF8, "application/json"), "definition");
            request.Content = form;
            JobUsage? recorded = null;
            double seconds = 0;
            if (jobId is not null)
            {
                if (usage is null || inspector is null) throw new InvalidOperationException("Job usage tracking is not configured.");
                seconds = await inspector.GetPcmDurationAsync(audioPath, linked.Token);
                recorded = await usage.StartAsync(jobId, "Transcription", linked.Token);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Speech transcription returned HTTP {StatusCode}.", (int)response.StatusCode);
                throw new TranscriptionException(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        "Speech authentication or permissions failed. Contact the administrator.",
                    System.Net.HttpStatusCode.TooManyRequests =>
                        "Speech service capacity was exceeded. Please try again later.",
                    System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.RequestEntityTooLarge =>
                        "Speech rejected the audio or transcription settings. Try a shorter video or contact the administrator.",
                    _ => "Speech transcription is temporarily unavailable. Please try again later."
                });
            }
            if (recorded is not null)
                await usage!.CompleteAsync(jobId!, recorded, seconds, 0, 0, 0, 0, linked.Token);
            var json = await response.Content.ReadAsStringAsync(linked.Token);
            return SpeechTranscriptionParser.Parse(json, settings.SourceLocale);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new TranscriptionException("Speech authentication failed. Contact the administrator.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionException("Speech could not be reached. Please try again later.", exception);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException("Speech transcription exceeded its time limit. Try a shorter video.", exception);
        }
    }
}
