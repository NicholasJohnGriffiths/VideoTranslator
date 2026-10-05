using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class TranscriptionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-speech-tests-{Guid.NewGuid():N}");
    private const string ValidJson =
        """{"durationMilliseconds":5000,"combinedPhrases":[{"text":"Do not use this block."}],"phrases":[{"offsetMilliseconds":2000,"durationMilliseconds":1000,"text":"Second sentence."},{"offsetMilliseconds":100,"durationMilliseconds":900,"text":"Hello world."}]}""";

    public TranscriptionTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task TrackedTranscriptionUsesInspectedAudioDuration()
    {
        var usage = new JobUsageService(new LocalJobUsageStorage(directory), Options.Create(new JobCostOptions()));
        var handler = new SpeechHandler();
        var inspector = new MediaAudioInspector(new DurationRunner(), Options.Create(new MediaOptions()));
        var speech = new AzureSpeechTranscriptionService(new HttpClient(handler), new TestCredential(),
            Options.Create(new AzureSpeechOptions { Enabled = true, Endpoint = "https://speech.example.cognitiveservices.azure.com/" }),
            NullLogger<AzureSpeechTranscriptionService>.Instance, usage, inspector);
        var path = Path.Combine(directory, "tracked.wav");
        await File.WriteAllBytesAsync(path, VoicePreviewTests.Wave());
        var id = Guid.NewGuid().ToString("N");
        await speech.TranscribeForJobAsync(id, path, CancellationToken.None);
        var cost = await usage.GetAsync(id, CancellationToken.None);
        Assert.Equal(5, Assert.Single(cost.CompletedRequests).Seconds);
        Assert.Equal(5m / 3600 * 0.6360m, cost.KnownNzd);
    }

    private sealed class DurationRunner : IMediaProcessRunner
    {
        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            Task.FromResult("""{"format":{"duration":"5"},"streams":[{"codec_name":"pcm_s16le","sample_rate":"16000","channels":1,"bits_per_sample":16}]}""");
    }

    [Fact]
    public void ParsesOrderedSegmentsUsingPhraseTimestampsNotCombinedText()
    {
        var transcript = SpeechTranscriptionParser.Parse(ValidJson, "en-NZ");
        Assert.Equal("en-NZ", transcript.SourceLanguage);
        Assert.Equal(2, transcript.Segments.Count);
        Assert.Equal(1, transcript.Segments[0].Sequence);
        Assert.Equal("Hello world.", transcript.Segments[0].OriginalText);
        Assert.Equal(TimeSpan.FromMilliseconds(100), transcript.Segments[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), transcript.Segments[0].End);
        Assert.Equal(2, transcript.Segments[1].Sequence);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), transcript.Segments[1].End);
        Assert.Empty(transcript.Segments[0].TranslatedText);
        Assert.Null(transcript.Segments[0].EditedText);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"durationMilliseconds":1000,"phrases":[]}""")]
    [InlineData("""{"durationMilliseconds":1000,"phrases":[{"text":"hello","offsetMilliseconds":-1,"durationMilliseconds":10}]}""")]
    [InlineData("""{"durationMilliseconds":1000,"phrases":[{"text":"hello","offsetMilliseconds":100,"durationMilliseconds":0}]}""")]
    [InlineData("""{"durationMilliseconds":1000,"phrases":[{"text":"hello","offsetMilliseconds":990,"durationMilliseconds":100}]}""")]
    [InlineData("""{"durationMilliseconds":1000,"phrases":[{"text":" ","offsetMilliseconds":1,"durationMilliseconds":10}]}""")]
    [InlineData("""{"durationMilliseconds":1000,"phrases":[{"text":"hello","offsetMilliseconds":1.5,"durationMilliseconds":10}]}""")]
    [InlineData("""{"durationMilliseconds":9223372036854775807,"phrases":[]}""")]
    public void InvalidAndEmptyResponsesAreExplicitFailures(string json)
    {
        Assert.Throws<TranscriptionException>(() => SpeechTranscriptionParser.Parse(json, "en-NZ"));
    }

    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(3723456, "01:02:03.456")]
    [InlineData(90000000, "25:00:00.000")]
    public void FormatsTimecodesWithoutWrappingHours(long milliseconds, string expected)
    {
        Assert.Equal(expected, TimecodeFormatter.Format(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void NegativeTimecodesAreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TimecodeFormatter.Format(TimeSpan.FromMilliseconds(-1)));

    [Fact]
    public async Task ServiceUsesKeylessMultipartEnglishRequestAndParsesResponse()
    {
        var handler = new SpeechHandler { Json = ValidJson };
        var audioPath = Path.Combine(directory, "audio.wav");
        await File.WriteAllBytesAsync(audioPath, new byte[100]);
        var transcript = await Service(handler).TranscribeAsync(audioPath, CancellationToken.None);
        Assert.Equal(2, transcript.Segments.Count);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-token", handler.Token);
        Assert.Equal("https://speech.example.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
            handler.Uri);
        Assert.Contains("audio/wav", handler.Body);
        Assert.Contains("original-audio.wav", handler.Body);
        Assert.Contains("\"locales\":[\"en-NZ\"]", handler.Body);
        Assert.Contains("multipart/form-data", handler.ContentType);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permissions")]
    [InlineData(HttpStatusCode.TooManyRequests, "capacity")]
    [InlineData(HttpStatusCode.BadRequest, "rejected")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task HttpFailuresUseSafeMessagesAndDoNotRetryBillableRequest(HttpStatusCode status, string expected)
    {
        var handler = new SpeechHandler { Status = status, Json = "secret service diagnostics" };
        var audioPath = Path.Combine(directory, "audio.wav");
        await File.WriteAllBytesAsync(audioPath, new byte[100]);
        var exception = await Assert.ThrowsAsync<TranscriptionException>(() =>
            Service(handler).TranscribeAsync(audioPath, CancellationToken.None));
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("secret", exception.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TimeoutIsSafeButCallerCancellationPropagates()
    {
        var audioPath = Path.Combine(directory, "audio.wav");
        await File.WriteAllBytesAsync(audioPath, new byte[100]);
        var handler = new SpeechHandler { Delay = TimeSpan.FromSeconds(20) };
        var exception = await Assert.ThrowsAsync<TranscriptionException>(() =>
            Service(handler, 1).TranscribeAsync(audioPath, CancellationToken.None));
        Assert.Contains("time limit", exception.Message);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(handler).TranscribeAsync(audioPath, cancellation.Token));
    }

    [Fact]
    public async Task EmptyAudioIsRejectedWithoutServiceCall()
    {
        var handler = new SpeechHandler();
        var audioPath = Path.Combine(directory, "audio.wav");
        await File.WriteAllBytesAsync(audioPath, []);
        await Assert.ThrowsAsync<TranscriptionException>(() =>
            Service(handler).TranscribeAsync(audioPath, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessorSavesTranscriptBeforeStatusAndUsesSavedTranscriptOnRecovery(bool alreadySaved)
    {
        var job = AudioReadyJob();
        if (alreadySaved)
        {
            job.TransitionTo(JobStatus.Transcribing);
        }
        var storage = new SpeechStorage(job)
        {
            Transcript = alreadySaved ? SpeechTranscriptionParser.Parse(ValidJson, "en-NZ") : null
        };
        var speech = new SpeechStub();
        await Processor(storage, speech).ProcessAsync(storage.Clone(), CancellationToken.None);
        Assert.Equal(JobStatus.TranscriptReady, storage.Job.Status);
        Assert.NotNull(storage.Transcript);
        Assert.Equal(alreadySaved ? 0 : 1, speech.Calls);
        Assert.Equal(alreadySaved
            ? new[] { "status:TranscriptReady" }
            : new[] { "status:Transcribing", "download", "transcript", "status:TranscriptReady" }, storage.Events);
        if (storage.DownloadDirectory is not null)
        {
            Assert.False(Directory.Exists(storage.DownloadDirectory));
        }
    }

    [Fact]
    public async Task ProcessorPersistsSafeFailureStage()
    {
        var storage = new SpeechStorage(AudioReadyJob());
        var speech = new SpeechStub { Failure = new TranscriptionException("No English speech recognised.") };
        await Processor(storage, speech).ProcessAsync(storage.Clone(), CancellationToken.None);
        Assert.Equal(JobStatus.Failed, storage.Job.Status);
        Assert.Equal(JobStatus.Transcribing, storage.Job.FailedAtStatus);
        Assert.Equal("No English speech recognised.", storage.Job.ErrorMessage);
        Assert.Null(storage.Transcript);
        Assert.False(Directory.Exists(storage.DownloadDirectory));
    }

    [Fact]
    public async Task CancellationAndStorageFailureLeaveDurablePendingState()
    {
        var storage = new SpeechStorage(AudioReadyJob());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Processor(storage, new SpeechStub { Failure = new OperationCanceledException() })
                .ProcessAsync(storage.Clone(), CancellationToken.None));
        Assert.Equal(JobStatus.Transcribing, storage.Job.Status);
        storage.FailSaving = true;
        await Assert.ThrowsAsync<JobStorageException>(() =>
            Processor(storage, new SpeechStub()).ProcessAsync(storage.Clone(), CancellationToken.None));
        Assert.Equal(JobStatus.Transcribing, storage.Job.Status);
        Assert.False(Directory.Exists(storage.DownloadDirectory));
    }

    [Fact]
    public void ActiveSpeechOptionsRequireEnglishCustomEndpointAndKnownCredentials()
    {
        var validator = new AzureSpeechOptionsValidator();
        Assert.True(validator.Validate(null, new AzureSpeechOptions()).Succeeded);
        Assert.True(validator.Validate(null, new AzureSpeechOptions { Enabled = true }).Failed);
        Assert.True(validator.Validate(null, new AzureSpeechOptions
        {
            Enabled = true, Endpoint = "https://speech.example.cognitiveservices.azure.com/",
            SourceLocale = "en-NZ", CredentialMode = "AzureCli"
        }).Succeeded);
        Assert.True(validator.Validate(null, new AzureSpeechOptions
        {
            Enabled = true, Endpoint = "https://speech.example.cognitiveservices.azure.com/",
            SourceLocale = "es-ES", CredentialMode = "AzureCli"
        }).Failed);
    }

    private static VideoJob AudioReadyJob()
    {
        var job = new VideoJob();
        job.TransitionTo(JobStatus.ExtractingAudio);
        job.TransitionTo(JobStatus.AudioReady);
        return job;
    }

    private static TranscriptionProcessor Processor(SpeechStorage storage, SpeechStub speech) =>
        new(storage, storage, speech, NullLogger<TranscriptionProcessor>.Instance);

    private static AzureSpeechTranscriptionService Service(SpeechHandler handler, int timeout = 10) =>
        new(new HttpClient(handler), new TestCredential(), Options.Create(new AzureSpeechOptions
        {
            Enabled = true, Endpoint = "https://speech.example.cognitiveservices.azure.com/",
            SourceLocale = "en-NZ", TimeoutSeconds = timeout
        }), NullLogger<AzureSpeechTranscriptionService>.Instance);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal("https://cognitiveservices.azure.com/.default", Assert.Single(requestContext.Scopes));
            cancellationToken.ThrowIfCancellationRequested();
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddMinutes(10));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class SpeechHandler : HttpMessageHandler
    {
        public string Json { get; init; } = ValidJson;
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public TimeSpan Delay { get; init; }
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? Token { get; private set; }
        public string? ContentType { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri!.ToString();
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            Token = request.Headers.Authorization?.Parameter;
            ContentType = request.Content!.Headers.ContentType!.ToString();
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            await Task.Delay(Delay, cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(Json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SpeechStub : ITranscriptionService
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }
        public Task<Transcript> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.True(File.Exists(audioPath));
            if (Failure is not null)
            {
                throw Failure;
            }
            return Task.FromResult(SpeechTranscriptionParser.Parse(ValidJson, "en-NZ"));
        }
    }

    private sealed class SpeechStorage(VideoJob job) : IJobStorageService, ITranscriptStorageService
    {
        public VideoJob Job { get; private set; } = job;
        public Transcript? Transcript { get; set; }
        public string? DownloadDirectory { get; private set; }
        public bool FailSaving { get; set; }
        public List<string> Events { get; } = [];
        public VideoJob Clone() => JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(Job))!;
        public Task CreateAsync(VideoJob job, Stream video, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoJob?> GetAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult<VideoJob?>(Clone());
        public IAsyncEnumerable<string> ListJobIdsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DownloadVideoAsync(VideoJob job, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAudioAsync(string jobId, string audioPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task DownloadAudioAsync(string jobId, string destinationPath, CancellationToken cancellationToken)
        {
            DownloadDirectory = Path.GetDirectoryName(destinationPath);
            Events.Add("download");
            await File.WriteAllBytesAsync(destinationPath, new byte[100], cancellationToken);
        }
        public Task SaveTranscriptAsync(string jobId, Transcript transcript, CancellationToken cancellationToken)
        {
            if (FailSaving)
            {
                throw new JobStorageException("Storage unavailable.", new IOException());
            }
            Events.Add("transcript");
            Transcript = transcript;
            return Task.CompletedTask;
        }
        public Task<Transcript?> GetTranscriptAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(Transcript);
        public Task UpdateAsync(VideoJob job, JobStatus expectedStatus, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedStatus, Job.Status);
            Job = JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(job))!;
            Events.Add($"status:{job.Status}");
            return Task.CompletedTask;
        }
    }
}
