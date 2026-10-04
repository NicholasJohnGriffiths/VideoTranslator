using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class AzureSpeechSynthesisService(
    HttpClient client, TokenCredential credential, IOptions<AzureSpeechOptions> options,
    ILogger<AzureSpeechSynthesisService> logger) : ISpeechSynthesisService
{
    public async Task<string> GenerateSpeechAsync(
        string text, LanguageOption language, string outputPath, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.SynthesisEnabled)
        {
            throw new SpeechSynthesisException("Voice synthesis is disabled in configuration.");
        }
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8000)
        {
            throw new SpeechSynthesisException("Voice preview needs non-blank text with at most 8000 characters.");
        }
        string ssml;
        try
        {
            XmlConvert.VerifyXmlChars(text);
            XNamespace ns = "http://www.w3.org/2001/10/synthesis";
            ssml = new XElement(ns + "speak", new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xml + "lang", language.SpeechLocale),
                new XElement(ns + "voice", new XAttribute("name", language.VoiceName), text))
                .ToString(SaveOptions.DisableFormatting);
        }
        catch (XmlException exception)
        {
            throw new SpeechSynthesisException("Preview text contains characters unsupported by Speech. Remove control characters and try again.", exception);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.SynthesisTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), linked.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(settings.Endpoint), "tts/cognitiveservices/v1"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"aad#{settings.ResourceId}#{token.Token}");
            request.Headers.Add("X-Microsoft-OutputFormat", SpeechWaveAudio.OutputFormat);
            request.Headers.UserAgent.ParseAdd("VideoTranslator/1.0");
            request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Speech synthesis returned HTTP {StatusCode}.", (int)response.StatusCode);
                throw new SpeechSynthesisException(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        "Speech synthesis authentication or permissions failed. Contact the administrator.",
                    System.Net.HttpStatusCode.TooManyRequests => "Speech synthesis capacity was exceeded. Try again later.",
                    System.Net.HttpStatusCode.BadRequest => "Speech rejected the text or configured voice. Contact the administrator.",
                    _ => "Speech synthesis is temporarily unavailable. Try again later."
                });
            }
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            var bytes = await SpeechWaveAudio.ReadBoundedAsync(stream, linked.Token);
            SpeechWaveAudio.Validate(bytes);
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(bytes, linked.Token);
            return outputPath;
        }
        catch (AuthenticationFailedException exception)
        {
            throw new SpeechSynthesisException("Speech synthesis authentication failed. Contact the administrator.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new SpeechSynthesisException("Speech synthesis could not be reached. Try again later.", exception);
        }
        catch (IOException exception)
        {
            throw new SpeechSynthesisException("Speech audio could not be read or written. Check storage and network access before trying again.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new SpeechSynthesisException("Speech returned invalid, empty or truncated audio. Try shorter text or contact the administrator.", exception);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SpeechSynthesisException("Speech synthesis exceeded its time limit. Try shorter text.", exception);
        }
    }
}
