using System.Net;
using System.Text;
using System.Text.Json;
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

public sealed class TranslationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-translation-tests-{Guid.NewGuid():N}");
    private static readonly CancellationToken None = CancellationToken.None;
    public TranslationTests() => Directory.CreateDirectory(directory);

    private static Transcript Source(int count = 2) => new()
    {
        Segments = Enumerable.Range(1, count).Select(sequence => new VideoSegment
        {
            Sequence = sequence, Start = TimeSpan.FromMilliseconds(sequence * 100),
            End = TimeSpan.FromMilliseconds(sequence * 100 + 500),
            OriginalText = $"English source {sequence}."
        }).ToList()
    };

    private static Translation Translated(string language = "ar-SA") => new()
    {
        TargetLanguage = language,
        Segments = Source().Segments.Select(segment => new VideoSegment
        {
            Sequence = segment.Sequence, Start = segment.Start, End = segment.End,
            OriginalText = segment.OriginalText, TranslatedText = $"Translated {segment.Sequence}"
        }).ToList()
    };

    private static string Response(string structured, string finish = "stop", string? refusal = null) =>
        JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish,
            message = new { content = structured, refusal } } } });

    [Fact]
    public void ParserMatchesIdsAndCopiesSourceTimingsNotModelData()
    {
        var source = Source();
        var parsed = TranslationResponseParser.Parse(Response(
            """{"segments":[{"sequence":2,"text":"second","start":"wrong"},{"sequence":1,"text":"first"}]}"""), source.Segments);
        Assert.Equal(new[] { "first", "second" }, parsed.Select(segment => segment.TranslatedText));
        Assert.All(parsed.Zip(source.Segments), pair =>
        {
            Assert.Equal(pair.Second.Sequence, pair.First.Sequence);
            Assert.Equal(pair.Second.Start, pair.First.Start);
            Assert.Equal(pair.Second.End, pair.First.End);
            Assert.Equal(pair.Second.OriginalText, pair.First.OriginalText);
            Assert.Null(pair.First.EditedText);
        });
    }

    [Theory]
    [InlineData("""{"segments":[]}""")]
    [InlineData("""{"segments":[{"sequence":1,"text":"ok"},{"sequence":1,"text":"duplicate"}]}""")]
    [InlineData("""{"segments":[{"sequence":1,"text":"ok"},{"sequence":3,"text":"unknown"}]}""")]
    [InlineData("""{"segments":[{"sequence":1,"text":""},{"sequence":2,"text":"ok"}]}""")]
    [InlineData("""{"segments":[{"sequence":"1","text":"ok"},{"sequence":2,"text":"ok"}]}""")]
    [InlineData("""{"segments":[{"sequence":1,"text":2},{"sequence":2,"text":"ok"}]}""")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void BadStructuredOutputFailsExplicitly(string structured) =>
        Assert.Throws<TranslationException>(() => TranslationResponseParser.Parse(Response(structured), Source().Segments));

    [Theory]
    [InlineData("length", null)]
    [InlineData("content_filter", null)]
    [InlineData("stop", "I cannot help.")]
    public void RefusalAndIncompleteOutputFail(string finish, string? refusal) =>
        Assert.Throws<TranslationException>(() => TranslationResponseParser.Parse(
            Response("""{"segments":[{"sequence":1,"text":"ok"},{"sequence":2,"text":"ok"}]}""", finish, refusal), Source().Segments));

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"choices":{}}""")]
    [InlineData("""{"choices":[{"finish_reason":3}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":false}}]}""")]
    public void BadEnvelopesFailExplicitly(string json) =>
        Assert.Throws<TranslationException>(() => TranslationResponseParser.Parse(json, Source().Segments));

    private static AzureOpenAIOptions Settings(bool enabled = true, int batch = 20, int timeout = 120) => new()
    {
        Enabled = enabled, Endpoint = "https://approved.openai.azure.com/", DeploymentName = "gpt-4o",
        CredentialMode = "AzureCli", PrivateEndpointAddresses = ["10.20.2.4"],
        BatchSegmentCount = batch, TimeoutSeconds = timeout
    };

    [Fact]
    public void PrivateConnectionAcceptsOnlyExactApprovedHostPortAndAddresses()
    {
        var connection = new PrivateOpenAIConnection(Options.Create(Settings()));
        connection.ValidateAddresses("approved.openai.azure.com", 443, [IPAddress.Parse("10.20.2.4")]);
        Assert.Throws<TranslationException>(() => connection.ValidateAddresses("approved.openai.azure.com", 443, []));
        Assert.Throws<TranslationException>(() => connection.ValidateAddresses("other.openai.azure.com", 443, [IPAddress.Parse("10.20.2.4")]));
        Assert.Throws<TranslationException>(() => connection.ValidateAddresses("approved.openai.azure.com", 80, [IPAddress.Parse("10.20.2.4")]));
        using var handler = connection.CreateHandler();
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData("20.213.196.14")]
    [InlineData("10.20.2.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("::ffff:10.20.2.4")]
    public void PublicAndUnapprovedPrivateDnsAreRejected(string address)
    {
        var connection = new PrivateOpenAIConnection(Options.Create(Settings()));
        Assert.Throws<TranslationException>(() => connection.ValidateAddresses(
            "approved.openai.azure.com", 443, [IPAddress.Parse(address)]));
        Assert.Throws<TranslationException>(() => connection.ValidateAddresses(
            "approved.openai.azure.com", 443, [IPAddress.Parse("10.20.2.4"), IPAddress.Parse(address)]));
    }

    [Fact]
    public void OptionsRequireApprovedPrivateAddressesAndExplicitAuthOnlyWhenEnabled()
    {
        var validator = new AzureOpenAIOptionsValidator();
        Assert.True(validator.Validate(null, new AzureOpenAIOptions()).Succeeded);
        Assert.True(validator.Validate(null, Settings()).Succeeded);
        Assert.True(validator.Validate(null, new AzureOpenAIOptions { Enabled = true }).Failed);
        Assert.True(validator.Validate(null, new AzureOpenAIOptions
        {
            Enabled = true, Endpoint = Settings().Endpoint, DeploymentName = "gpt-4o",
            PrivateEndpointAddresses = ["20.213.196.14"]
        }).Failed);
    }

    private static AzureOpenAITranslationService Service(OpenAIHandler handler, AzureOpenAIOptions? settings = null) =>
        new(new HttpClient(handler), new Credential(), Options.Create(settings ?? Settings()),
            NullLogger<AzureOpenAITranslationService>.Instance);

    [Theory]
    [InlineData("zh-CN", "Mandarin Chinese")]
    [InlineData("ar-SA", "Arabic")]
    [InlineData("es-ES", "Spanish")]
    [InlineData("hi-IN", "Hindi")]
    public async Task ServiceUsesKeylessStructuredBatchesForAllFourLanguages(string code, string name)
    {
        var handler = new OpenAIHandler();
        var translation = await Service(handler, Settings(batch: 1)).TranslateAsync(Source(),
            new LanguageOption { Code = code, DisplayName = name }, None);
        Assert.Equal(code, translation.TargetLanguage);
        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.Equal("Bearer test-token", request.Authorization);
            Assert.Equal("https://approved.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21", request.Uri);
            using var payload = JsonDocument.Parse(request.Body);
            Assert.Equal("json_schema", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            Assert.True(payload.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
            using var user = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Assert.Equal(code, user.RootElement.GetProperty("targetLocale").GetString());
            Assert.Equal(name, user.RootElement.GetProperty("targetLanguage").GetString());
        }
        TranslationMetadata.ValidateAgainstTranscript(translation, Source(), code);
    }

    [Fact]
    public async Task CharacterLimitSplitsBatchesAndRejectsOversizedSegmentsWithoutCalls()
    {
        var handler = new OpenAIHandler();
        var settings = new AzureOpenAIOptions
        {
            Enabled = true, Endpoint = Settings().Endpoint, DeploymentName = "gpt-4o", BatchCharacterLimit = 1000
        };
        var source = new Transcript
        {
            Segments = Source().Segments.Select(segment => new VideoSegment
            {
                Sequence = segment.Sequence, Start = segment.Start, End = segment.End, OriginalText = new string('a', 600)
            }).ToList()
        };
        await Service(handler, settings).TranslateAsync(source, new LanguageOption { Code = "es-ES" }, None);
        Assert.Equal(2, handler.Requests.Count);
        var oversized = new Transcript { Segments = [new VideoSegment
        {
            Sequence = 1, End = TimeSpan.FromSeconds(1), OriginalText = new string('a', 1001)
        }] };
        await Assert.ThrowsAsync<TranslationException>(() =>
            Service(handler, settings).TranslateAsync(oversized, new LanguageOption { Code = "es-ES" }, None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permissions")]
    [InlineData(HttpStatusCode.Unauthorized, "permissions")]
    [InlineData(HttpStatusCode.TooManyRequests, "capacity")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unsuccessful")]
    public async Task HttpFailuresAreSafeAndNeverAutomaticallyRetried(HttpStatusCode status, string expected)
    {
        var handler = new OpenAIHandler { Status = status };
        var exception = await Assert.ThrowsAsync<TranslationException>(() =>
            Service(handler).TranslateAsync(Source(), new LanguageOption { Code = "es-ES" }, None));
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("secret", exception.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DisabledServiceNeverCallsOpenAI()
    {
        var handler = new OpenAIHandler();
        await Assert.ThrowsAsync<TranslationException>(() =>
            Service(handler, Settings(enabled: false)).TranslateAsync(Source(), new LanguageOption(), None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AuthenticationFailureIsSafeAndMakesNoOpenAIRequest()
    {
        var handler = new OpenAIHandler();
        var service = new AzureOpenAITranslationService(new HttpClient(handler), new FailingCredential(),
            Options.Create(Settings()), NullLogger<AzureOpenAITranslationService>.Instance);
        var exception = await Assert.ThrowsAsync<TranslationException>(() =>
            service.TranslateAsync(Source(), new LanguageOption(), None));
        Assert.Contains("authentication", exception.Message);
        Assert.DoesNotContain("secret", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkAndOversizedResponseFailuresAreExplicit(bool oversized)
    {
        var handler = new OpenAIHandler { NetworkFailure = !oversized, Oversized = oversized };
        var exception = await Assert.ThrowsAsync<TranslationException>(() =>
            Service(handler).TranslateAsync(Source(), new LanguageOption(), None));
        Assert.Contains(oversized ? "oversized" : "private connection", exception.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void OversizedTranslationTextFailsInsteadOfSavingUneditableOutput()
    {
        var structured = JsonSerializer.Serialize(new { segments = new[]
        {
            new { sequence = 1, text = new string('a', 8001) }, new { sequence = 2, text = "ok" }
        } });
        Assert.Throws<TranslationException>(() =>
            TranslationResponseParser.Parse(Response(structured), Source().Segments));
    }

    [Fact]
    public async Task TimeoutFailsButCallerCancellationPropagates()
    {
        var handler = new OpenAIHandler { Delay = TimeSpan.FromSeconds(20) };
        var exception = await Assert.ThrowsAsync<TranslationException>(() =>
            Service(handler, Settings(timeout: 1)).TranslateAsync(Source(), new LanguageOption(), None));
        Assert.Contains("time limit", exception.Message);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(handler).TranslateAsync(Source(), new LanguageOption(), cancelled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoragePreservesTranslationEditsResetAndRejectsStaleSaves(bool blob)
    {
        using var harness = new BlobStorageTests.BlobHarness();
        ITranslationStorageService storage = blob ? new BlobTranslationStorageService(harness.Container) : Local();
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(directory, "jobs", id));
        Assert.Null(await storage.GetTranslationAsync(id, None));
        Assert.Null((await storage.GetEditsAsync(id, None)).Revision);
        await storage.SaveTranslationAsync(id, Translated(), None);
        var first = new ScriptEdits { Segments = new() { [1] = "", [2] = "<script>untrusted</script>" } };
        await storage.SaveEditsAsync(id, first, null, None);
        var snapshot = await storage.GetEditsAsync(id, None);
        Assert.NotNull(snapshot.Revision);
        Assert.Equal("", snapshot.Edits.Segments[1]);
        await Assert.ThrowsAsync<ScriptConflictException>(() => storage.SaveEditsAsync(id, first, null, None));
        var reset = new ScriptEdits { Segments = new() { [1] = null, [2] = "Unicode \u0639\u0631\u0628\u064a" } };
        await storage.SaveEditsAsync(id, reset, snapshot.Revision, None);
        await Assert.ThrowsAsync<ScriptConflictException>(() => storage.SaveEditsAsync(id, first, snapshot.Revision, None));
        var after = await storage.GetEditsAsync(id, None);
        Assert.Null(after.Edits.Segments[1]);
        Assert.Equal(reset.Segments[2], after.Edits.Segments[2]);
        var immutable = await storage.GetTranslationAsync(id, None);
        Assert.NotNull(immutable);
        Assert.All(immutable.Segments, segment => Assert.Null(segment.EditedText));
        Assert.Equal("Translated 1", immutable.Segments[0].TranslatedText);
        if (blob)
        {
            await Assert.ThrowsAsync<JobStorageException>(() => storage.SaveTranslationAsync(id, Translated(), None));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => storage.SaveTranslationAsync(id, Translated(), None));
            var persisted = await Local().GetEditsAsync(id, None);
            Assert.Equal(after.Revision, persisted.Revision);
        }
        var bad = new ScriptEdits { Segments = new() { [1] = "missing second" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.SaveEditsAsync(id, bad, after.Revision, None));
        Assert.Equal(after.Revision, (await storage.GetEditsAsync(id, None)).Revision);
    }

    [Fact]
    public async Task BlobMissingContainerAndPermissionsAreNotTreatedAsMissingTranslation()
    {
        using var harness = new BlobStorageTests.BlobHarness();
        var storage = new BlobTranslationStorageService(harness.Container);
        var id = Guid.NewGuid().ToString("N");
        harness.Handler.MissingContainer = true;
        await Assert.ThrowsAsync<JobStorageException>(() => storage.GetTranslationAsync(id, None));
        harness.Handler.MissingContainer = false;
        harness.Handler.DenyReads = true;
        await Assert.ThrowsAsync<JobStorageException>(() => storage.GetEditsAsync(id, None));
    }

    [Fact]
    public async Task ConcurrentLocalSavesAllowOnlyOneWinnerEvenForIdenticalEdits()
    {
        var storage = Local();
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(directory, "jobs", id));
        await storage.SaveTranslationAsync(id, Translated(), None);
        var edits = new ScriptEdits { Segments = new() { [1] = "first", [2] = null } };
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try { await storage.SaveEditsAsync(id, edits, null, None); return true; }
            catch (ScriptConflictException) { return false; }
        }));
        Assert.Single(outcomes, result => result);
    }

    [Fact]
    public async Task ProcessorSavesBeforeReviewAndReusesCommittedTranslationAfterInterruption()
    {
        var job = ReadyJob();
        var state = new ProcessorStorage(job);
        var translator = new TranslatorStub();
        await Processor(state, translator).ProcessAsync(state.Clone(), None);
        Assert.Equal(JobStatus.AwaitingScriptReview, state.Job.Status);
        Assert.Equal(new[] { "Translating", "translation", "AwaitingScriptReview" }, state.Events);
        Assert.Equal(1, translator.Calls);

        var interrupted = ReadyJob();
        interrupted.TransitionTo(JobStatus.Translating);
        var recovery = new ProcessorStorage(interrupted) { Translation = Translated() };
        await Processor(recovery, translator).ProcessAsync(recovery.Clone(), None);
        Assert.Equal(JobStatus.AwaitingScriptReview, recovery.Job.Status);
        Assert.Equal(1, translator.Calls);
    }

    [Fact]
    public async Task ProcessorDoesNotAdvanceAfterFailedStorageAndReportsTranslationFailure()
    {
        var state = new ProcessorStorage(ReadyJob()) { FailSaving = true };
        await Assert.ThrowsAsync<JobStorageException>(() =>
            Processor(state, new TranslatorStub()).ProcessAsync(state.Clone(), None));
        Assert.Equal(JobStatus.Translating, state.Job.Status);
        Assert.Null(state.Translation);
        var failed = new ProcessorStorage(ReadyJob());
        await Processor(failed, new TranslatorStub { Failure = new TranslationException("Private route unavailable.") })
            .ProcessAsync(failed.Clone(), None);
        Assert.Equal(JobStatus.Failed, failed.Job.Status);
        Assert.Equal(JobStatus.Translating, failed.Job.FailedAtStatus);
        Assert.Contains("Private route", failed.Job.ErrorMessage);
    }

    [Fact]
    public void MetadataRejectsChangedTimingsLanguageAndOversizedEdits()
    {
        Assert.Throws<InvalidDataException>(() => TranslationMetadata.ValidateAgainstTranscript(Translated(), Source(), "es-ES"));
        var changed = new Transcript { Segments = Source().Segments.Select(segment => new VideoSegment
        {
            Sequence = segment.Sequence, Start = segment.Start, End = segment.End + TimeSpan.FromMilliseconds(1),
            OriginalText = segment.OriginalText
        }).ToList() };
        Assert.Throws<InvalidDataException>(() => TranslationMetadata.ValidateAgainstTranscript(Translated(), changed, "ar-SA"));
        Assert.Throws<InvalidDataException>(() => TranslationMetadata.ValidateEdits(Translated(), new ScriptEdits
        {
            Segments = new() { [1] = new string('a', 8001), [2] = null }
        }));
    }

    private LocalTranslationStorageService Local() =>
        new(new TestEnvironment { ContentRootPath = directory }, Options.Create(new LocalStorageOptions { RootPath = "jobs" }));

    private static VideoJob ReadyJob()
    {
        var job = new VideoJob { SelectedLanguage = "ar-SA" };
        foreach (var status in new[] { JobStatus.ExtractingAudio, JobStatus.AudioReady, JobStatus.Transcribing, JobStatus.TranscriptReady })
        {
            job.TransitionTo(status);
        }
        return job;
    }

    private static TranslationProcessor Processor(ProcessorStorage storage, TranslatorStub translator) => new(
        storage, storage, storage, translator, new LanguageService(Options.Create(new LanguageOptions
        {
            Supported = [LanguageTests.Language("ar-SA")]
        })), NullLogger<TranslationProcessor>.Instance);

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

    private sealed class OpenAIHandler : HttpMessageHandler
    {
        public List<(string Uri, string Body, string Authorization)> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public TimeSpan Delay { get; init; }
        public bool NetworkFailure { get; init; }
        public bool Oversized { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization!.ToString()));
            if (NetworkFailure) { throw new HttpRequestException("Synthetic connection failure"); }
            if (Oversized)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('a', 1024 * 1024 + 1)) };
            }
            await Task.Delay(Delay, cancellationToken);
            using var payload = JsonDocument.Parse(body);
            using var data = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var content = JsonSerializer.Serialize(new
            {
                segments = data.RootElement.GetProperty("segments").EnumerateArray().Select(segment => new
                {
                    sequence = segment.GetProperty("sequence").GetInt32(), text = "Translation \u0639\u0631\u0628\u064a"
                }).ToArray()
            });
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Status == HttpStatusCode.OK ? Response(content) : "secret diagnostics", Encoding.UTF8, "application/json")
            };
        }

    }

    private sealed class FailingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("secret credential diagnostics");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("secret credential diagnostics");
    }

    private sealed class TranslatorStub : ITranslationService
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }
        public Task<Translation> TranslateAsync(Transcript transcript, LanguageOption language, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(Translated(language.Code));
        }
    }

    private sealed class ProcessorStorage(VideoJob job) : IJobStorageService, ITranscriptStorageService, ITranslationStorageService
    {
        public VideoJob Job { get; private set; } = job;
        public Translation? Translation { get; set; }
        public bool FailSaving { get; init; }
        public List<string> Events { get; } = [];
        public VideoJob Clone() => JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(Job))!;
        public Task CreateAsync(VideoJob value, Stream video, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoJob?> GetAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult<VideoJob?>(Clone());
        public IAsyncEnumerable<string> ListJobIdsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DownloadVideoAsync(VideoJob value, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAudioAsync(string jobId, string audioPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DownloadAudioAsync(string jobId, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveTranscriptAsync(string jobId, Transcript transcript, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Transcript?> GetTranscriptAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult<Transcript?>(Source());
        public Task UpdateAsync(VideoJob value, JobStatus expectedStatus, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedStatus, Job.Status);
            Job = JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(value))!;
            Events.Add(value.Status.ToString());
            return Task.CompletedTask;
        }
        public Task<Translation?> GetTranslationAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(Translation);
        public Task SaveTranslationAsync(string jobId, Translation translation, CancellationToken cancellationToken)
        {
            if (FailSaving) { throw new JobStorageException("Storage unavailable.", new IOException()); }
            Events.Add("translation"); Translation = translation; return Task.CompletedTask;
        }
        public Task<ScriptEditSnapshot> GetEditsAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveEditsAsync(string jobId, ScriptEdits edits, string? revision, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "TranslationTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
