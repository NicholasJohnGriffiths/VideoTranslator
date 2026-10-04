using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class VoicePreviewTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-voice-tests-{Guid.NewGuid():N}");
    private static readonly CancellationToken None = CancellationToken.None;
    public VoicePreviewTests() => Directory.CreateDirectory(directory);

    internal static byte[] Wave(int samples = 16000)
    {
        var bytes = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples * 2);
        return bytes;
    }

    private static AzureSpeechOptions Settings(bool enabled = true, int timeout = 120) => new()
    {
        SynthesisEnabled = enabled, Endpoint = "https://speech.example.cognitiveservices.azure.com/",
        ResourceId = "/subscriptions/e8f54ba3-a8f1-464f-8ad4-9367e5b7a3aa/resourceGroups/tests/providers/Microsoft.CognitiveServices/accounts/speech",
        SynthesisTimeoutSeconds = timeout
    };
    private static LanguageOption Language(string code = "ar-SA", string voice = "ar-SA-ZariyahNeural") =>
        new() { Code = code, SpeechLocale = code, VoiceName = voice };
    private static AzureSpeechSynthesisService Service(Handler handler, AzureSpeechOptions? options = null) => new(
        new HttpClient(handler), new Credential(), Options.Create(options ?? Settings()),
        NullLogger<AzureSpeechSynthesisService>.Instance);

    [Theory]
    [InlineData("zh-CN", "zh-CN-XiaoxiaoNeural")]
    [InlineData("ar-SA", "ar-SA-ZariyahNeural")]
    [InlineData("es-ES", "es-ES-ElviraNeural")]
    [InlineData("hi-IN", "hi-IN-SwaraNeural")]
    public async Task SynthesisUsesKeylessCustomEndpointAndEscapesTextNotUserSsml(string locale, string voice)
    {
        var handler = new Handler();
        var path = Path.Combine(directory, $"{locale}.wav");
        var text = "Unicode \u0639\u0631\u0628\u064a & <audio src=\"https://untrusted.test/\">literal</audio>";
        Assert.Equal(path, await Service(handler).GenerateSpeechAsync(text, Language(locale, voice), path, None));
        Assert.Equal("https://speech.example.cognitiveservices.azure.com/tts/cognitiveservices/v1", handler.Uri);
        Assert.Equal($"Bearer aad#{Settings().ResourceId}#test-token", handler.Authorization);
        Assert.Equal(SpeechWaveAudio.OutputFormat, handler.Format);
        Assert.StartsWith("application/ssml+xml", handler.ContentType);
        var ssml = XDocument.Parse(handler.Body!);
        XNamespace ns = "http://www.w3.org/2001/10/synthesis";
        Assert.Equal(locale, ssml.Root!.Attribute(XNamespace.Xml + "lang")!.Value);
        var element = Assert.Single(ssml.Root.Elements());
        Assert.Equal(ns + "voice", element.Name);
        Assert.Equal(voice, element.Attribute("name")!.Value);
        Assert.Equal(text, element.Value);
        Assert.Empty(element.Elements());
        Assert.Equal(TimeSpan.FromSeconds(1), SpeechWaveAudio.Validate(await File.ReadAllBytesAsync(path)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\0")]
    public async Task InvalidTextNeverCallsSpeech(string text)
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<SpeechSynthesisException>(() =>
            Service(handler).GenerateSpeechAsync(text, Language(), Path.Combine(directory, "bad.wav"), None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DisabledAndOversizedTextNeverCallSpeech()
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<SpeechSynthesisException>(() =>
            Service(handler, Settings(false)).GenerateSpeechAsync("hello", Language(), "unused.wav", None));
        await Assert.ThrowsAsync<SpeechSynthesisException>(() =>
            Service(handler).GenerateSpeechAsync(new string('a', 8001), Language(), "unused.wav", None));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permissions")]
    [InlineData(HttpStatusCode.Unauthorized, "permissions")]
    [InlineData(HttpStatusCode.TooManyRequests, "capacity")]
    [InlineData(HttpStatusCode.BadRequest, "rejected")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task HttpErrorsNeverRetryOrLeakDiagnostics(HttpStatusCode status, string expected)
    {
        var handler = new Handler { Status = status };
        var exception = await Assert.ThrowsAsync<SpeechSynthesisException>(() =>
            Service(handler).GenerateSpeechAsync("hello", Language(), Path.Combine(directory, "error.wav"), None));
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("secret", exception.Message);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task BadAudioIsNotSavedAndTimeoutIsDistinctFromCallerCancellation()
    {
        var bad = new Handler { Audio = [1, 2, 3] };
        await Assert.ThrowsAsync<SpeechSynthesisException>(() =>
            Service(bad).GenerateSpeechAsync("hello", Language(), Path.Combine(directory, "bad.wav"), None));
        Assert.Empty(Directory.GetFiles(directory));
        var delayed = new Handler { Delay = TimeSpan.FromSeconds(20) };
        var exception = await Assert.ThrowsAsync<SpeechSynthesisException>(() =>
            Service(delayed, Settings(timeout: 1)).GenerateSpeechAsync("hello", Language(), Path.Combine(directory, "timeout.wav"), None));
        Assert.Contains("time limit", exception.Message);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(delayed).GenerateSpeechAsync("hello", Language(), Path.Combine(directory, "cancel.wav"), cancelled.Token));
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotCallSpeechOrRevealCredentialDiagnostics()
    {
        var handler = new Handler();
        var service = new AzureSpeechSynthesisService(new HttpClient(handler), new FailingCredential(),
            Options.Create(Settings()), NullLogger<AzureSpeechSynthesisService>.Instance);
        var exception = await Assert.ThrowsAsync<SpeechSynthesisException>(() =>
            service.GenerateSpeechAsync("hello", Language(), Path.Combine(directory, "auth.wav"), None));
        Assert.Contains("authentication", exception.Message);
        Assert.DoesNotContain("secret", exception.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PreviewCancellationReleasesGateAndRemovesTemporaryAudio()
    {
        var speech = new SpeechStub { Wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = PreviewService(speech, new StorageStub());
        using var cancelled = new CancellationTokenSource();
        var first = service.GenerateAsync(ReviewJob(), Segment(), "cancelled text", Language(), cancelled.Token);
        await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(Directory.Exists(speech.LastDirectory));
        speech.Wait.SetResult();
        var next = await service.GenerateAsync(ReviewJob(), Segment(), "next text", Language(), None);
        Assert.False(next.Reused);
    }

    [Fact]
    public async Task PreviewRejectsWrongJobStageOrLanguageWithoutBillableCalls()
    {
        var speech = new SpeechStub();
        var service = PreviewService(speech, new StorageStub());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAsync(new VideoJob { SelectedLanguage = "ar-SA" }, Segment(), "text", Language(), None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAsync(ReviewJob(), Segment(), "text", Language("es-ES"), None));
        Assert.Equal(0, speech.Calls);
    }

    [Fact]
    public async Task LocalPreviewRejectsCorruptCachedAudioAndOversizedReads()
    {
        var job = ReviewJob();
        var key = VoicePreviewKey.Create("text", Language());
        var previews = Path.Combine(directory, "jobs", job.JobId, "previews");
        Directory.CreateDirectory(previews);
        await File.WriteAllBytesAsync(Path.Combine(previews, $"1-{key}.wav"), [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Local().GetAsync(job.JobId, 1, key, None));
        await using var oversized = new MemoryStream(new byte[SpeechWaveAudio.MaxBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SpeechWaveAudio.ReadBoundedAsync(oversized, None));
    }

    [Fact]
    public void WaveValidationRejectsTruncatedEmptyIncorrectFormatAndServiceLimit()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), SpeechWaveAudio.Validate(Wave()));
        Assert.Throws<InvalidDataException>(() => SpeechWaveAudio.Validate(Wave()[..^1]));
        Assert.Throws<InvalidDataException>(() => SpeechWaveAudio.Validate(Wave(0)));
        var stereo = Wave(); stereo[22] = 2;
        Assert.Throws<InvalidDataException>(() => SpeechWaveAudio.Validate(stereo));
        var badRate = Wave(); badRate[24] = 0;
        Assert.Throws<InvalidDataException>(() => SpeechWaveAudio.Validate(badRate));
        var badRiff = Wave(); badRiff[0] = 0;
        Assert.Throws<InvalidDataException>(() => SpeechWaveAudio.Validate(badRiff));
        Assert.Throws<InvalidDataException>(() => SpeechWaveAudio.Validate(Wave(16000 * 600)));
    }

    [Fact]
    public void CacheKeyDependsOnExactTextLocaleVoiceAndFormat()
    {
        var key = VoicePreviewKey.Create("text", Language());
        Assert.Equal(64, key.Length);
        Assert.Equal(key, VoicePreviewKey.Create("text", Language()));
        Assert.NotEqual(key, VoicePreviewKey.Create("text ", Language()));
        Assert.NotEqual(key, VoicePreviewKey.Create("text", Language("es-ES")));
        Assert.NotEqual(key, VoicePreviewKey.Create("text", Language(voice: "another")));
        VoicePreviewKey.Validate(Guid.NewGuid().ToString("N"), 1, key);
        Assert.Throws<ArgumentException>(() => VoicePreviewKey.Validate("../outside", 1, key));
        Assert.Throws<ArgumentException>(() => VoicePreviewKey.Validate(Guid.NewGuid().ToString("N"), 0, key));
        Assert.Throws<ArgumentException>(() => VoicePreviewKey.Validate(Guid.NewGuid().ToString("N"), 1, "../outside"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewStoragePersistsPrivateImmutableAudioAcrossInstances(bool blob)
    {
        using var harness = new BlobStorageTests.BlobHarness();
        var job = ReviewJob();
        var jobDirectory = Path.Combine(directory, "jobs", job.JobId);
        Directory.CreateDirectory(jobDirectory);
        await File.WriteAllTextAsync(Path.Combine(jobDirectory, "job.json"), "{}");
        IVoicePreviewStorageService storage = blob ? new BlobVoicePreviewStorageService(harness.Container) : Local();
        var key = VoicePreviewKey.Create("text", Language());
        Assert.Null(await storage.GetAsync(job.JobId, 1, key, None));
        await storage.SaveAsync(job.JobId, 1, key, Wave(), None);
        Assert.Equal(Wave(), await storage.GetAsync(job.JobId, 1, key, None));
        IVoicePreviewStorageService restarted = blob ? new BlobVoicePreviewStorageService(harness.Container) : Local();
        Assert.Equal(Wave(), await restarted.GetAsync(job.JobId, 1, key, None));
        if (blob)
        {
            await Assert.ThrowsAsync<JobStorageException>(() => storage.SaveAsync(job.JobId, 1, key, Wave(), None));
            harness.Handler.DenyReads = true;
            await Assert.ThrowsAsync<JobStorageException>(() => storage.GetAsync(job.JobId, 1, key, None));
            harness.Handler.DenyReads = false;
            harness.Handler.MissingContainer = true;
            await Assert.ThrowsAsync<JobStorageException>(() => storage.GetAsync(job.JobId, 1, key, None));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync(job.JobId, 1, key, Wave(), None));
        }
    }

    [Fact]
    public async Task PreviewReusesCachedAudioAndDoesNotMutateJobOrSegment()
    {
        var speech = new SpeechStub();
        var storage = new StorageStub();
        var service = PreviewService(speech, storage);
        var job = ReviewJob();
        var segment = Segment();
        var first = await service.GenerateAsync(job, segment, "unsaved edit", Language(), None);
        Assert.False(first.Reused);
        Assert.True((await service.GenerateAsync(job, segment, "unsaved edit", Language(), None)).Reused);
        Assert.Equal(1, speech.Calls);
        await service.GenerateAsync(job, segment, "different edit", Language(), None);
        Assert.Equal(2, speech.Calls);
        Assert.Equal(JobStatus.AwaitingScriptReview, job.Status);
        Assert.Equal("machine translation", segment.TranslatedText);
        Assert.Null(segment.EditedText);
        Assert.False(Directory.Exists(speech.LastDirectory));
    }

    [Fact]
    public async Task PreviewRejectsBlankAndConcurrentRequestsAndPropagatesStorageFailures()
    {
        var speech = new SpeechStub { Wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var storage = new StorageStub();
        var service = PreviewService(speech, storage);
        var job = ReviewJob();
        await Assert.ThrowsAsync<SpeechSynthesisException>(() => service.GenerateAsync(job, Segment(), "", Language(), None));
        Assert.Equal(0, speech.Calls);
        var first = service.GenerateAsync(job, Segment(), "text", Language(), None);
        await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<VoicePreviewBusyException>(() => service.GenerateAsync(job, Segment(), "second", Language(), None));
        speech.Wait.SetResult();
        await first;
        storage.FailSaving = true;
        await Assert.ThrowsAsync<JobStorageException>(() => service.GenerateAsync(job, Segment(), "new text", Language(), None));
        Assert.False(Directory.Exists(speech.LastDirectory));
        Assert.Equal(JobStatus.AwaitingScriptReview, job.Status);
    }

    [Fact]
    public void SynthesisOptionsRequireResourceIdEvenWithTranscriptionDisabled()
    {
        var validator = new AzureSpeechOptionsValidator();
        Assert.True(validator.Validate(null, new AzureSpeechOptions()).Succeeded);
        Assert.True(validator.Validate(null, Settings()).Succeeded);
        Assert.True(validator.Validate(null, new AzureSpeechOptions { SynthesisEnabled = true, Endpoint = Settings().Endpoint }).Failed);
    }

    private LocalVoicePreviewStorageService Local() => new(new TestEnvironment { ContentRootPath = directory },
        Options.Create(new LocalStorageOptions { RootPath = "jobs" }));
    private static VoicePreviewService PreviewService(SpeechStub speech, StorageStub storage) =>
        new(speech, storage, NullLogger<VoicePreviewService>.Instance);
    private static VideoSegment Segment() => new()
    {
        Sequence = 1, End = TimeSpan.FromSeconds(2), OriginalText = "English", TranslatedText = "machine translation"
    };
    private static VideoJob ReviewJob()
    {
        var job = new VideoJob { SelectedLanguage = "ar-SA" };
        foreach (var status in new[] { JobStatus.ExtractingAudio, JobStatus.AudioReady, JobStatus.Transcribing,
            JobStatus.TranscriptReady, JobStatus.Translating, JobStatus.AwaitingScriptReview }) { job.TransitionTo(status); }
        return job;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Format { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }
        public byte[] Audio { get; init; } = Wave();
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public TimeSpan Delay { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization!.ToString();
            Format = request.Headers.GetValues("X-Microsoft-OutputFormat").Single();
            ContentType = request.Content!.Headers.ContentType!.ToString();
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            await Task.Delay(Delay, cancellationToken);
            return new HttpResponseMessage(Status) { Content = new ByteArrayContent(
                Status == HttpStatusCode.OK ? Audio : Encoding.UTF8.GetBytes("secret diagnostics")) };
        }
    }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal("https://cognitiveservices.azure.com/.default", Assert.Single(requestContext.Scopes));
            cancellationToken.ThrowIfCancellationRequested();
            return new("test-token", DateTimeOffset.UtcNow.AddMinutes(10));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class FailingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("secret identity details");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("secret identity details");
    }

    private sealed class SpeechStub : ISpeechSynthesisService
    {
        public int Calls { get; private set; }
        public string? LastDirectory { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Wait { get; init; }
        public async Task<string> GenerateSpeechAsync(string text, LanguageOption language, string outputPath, CancellationToken cancellationToken)
        {
            Calls++; LastDirectory = Path.GetDirectoryName(outputPath);
            Started.TrySetResult();
            if (Wait is not null) { await Wait.Task.WaitAsync(cancellationToken); }
            await File.WriteAllBytesAsync(outputPath, Wave(), cancellationToken);
            return outputPath;
        }
    }
    private sealed class StorageStub : IVoicePreviewStorageService
    {
        private readonly Dictionary<string, byte[]> bytes = [];
        public bool FailSaving { get; set; }
        public Task<byte[]?> GetAsync(string jobId, int sequence, string key, CancellationToken cancellationToken) =>
            Task.FromResult(bytes.GetValueOrDefault(key));
        public Task SaveAsync(string jobId, int sequence, string key, byte[] audio, CancellationToken cancellationToken)
        {
            if (FailSaving) { throw new JobStorageException("Storage unavailable", new IOException()); }
            bytes.Add(key, audio); return Task.CompletedTask;
        }
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "VoicePreviewTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
