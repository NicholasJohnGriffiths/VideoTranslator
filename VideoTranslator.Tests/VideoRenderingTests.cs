using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Pages;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class VideoRenderingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-render-tests-{Guid.NewGuid():N}");
    private static readonly CancellationToken None = CancellationToken.None;
    public VideoRenderingTests() => Directory.CreateDirectory(directory);
    private static SpeechGeneration Script() => new()
    {
        Language = new() { Code = "ar-SA", SpeechLocale = "ar-SA", VoiceName = "ar-SA-ZariyahNeural" },
        Segments = [
            new() { Sequence = 1, Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2),
                OriginalText = "English", TranslatedText = "مرحبا <literal> & text\n\nnext" },
            new() { Sequence = 2, Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4), OriginalText = "Blank edit", TranslatedText = "" }
        ]
    };
    private static VideoJob Job(JobStatus status = JobStatus.CreatingSubtitles)
    {
        var job = new VideoJob { SelectedLanguage = "ar-SA" };
        foreach (var step in new[] { JobStatus.ExtractingAudio, JobStatus.AudioReady, JobStatus.Transcribing,
            JobStatus.TranscriptReady, JobStatus.Translating, JobStatus.AwaitingScriptReview }) { job.TransitionTo(step); }
        job.ApproveSpeech(Script());
        job.TransitionTo(JobStatus.TranslatedAudioReady);
        if (status is JobStatus.CreatingSubtitles or JobStatus.RenderingVideo or JobStatus.Completed)
        { job.TransitionTo(JobStatus.CreatingSubtitles); }
        if (status is JobStatus.RenderingVideo or JobStatus.Completed) { job.TransitionTo(JobStatus.RenderingVideo); }
        if (status == JobStatus.Completed) { job.TransitionTo(JobStatus.Completed); }
        return job;
    }

    [Theory]
    [InlineData("مرحبا")]
    [InlineData("你好")]
    [InlineData("नमस्ते")]
    [InlineData("¡Hola!")]
    public async Task WebVttUsesEffectiveUnicodeTextEscapesMarkupAndOmitsBlankCues(string text)
    {
        var segment = new VideoSegment
        {
            Sequence = 1, Start = TimeSpan.FromMilliseconds(3600001), End = TimeSpan.FromMilliseconds(3601002),
            TranslatedText = "machine", EditedText = text + " <b> & -->\n\nnext"
        };
        var path = Path.Combine(directory, "subtitles.vtt");
        await new WebVttSubtitleService().GenerateWebVttAsync([segment,
            new() { Sequence = 2, Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(3), TranslatedText = "hidden", EditedText = "" }], path, None);
        var bytes = await File.ReadAllBytesAsync(path);
        var result = await File.ReadAllTextAsync(path);
        Assert.StartsWith("WEBVTT\n\n1\n01:00:00.001 --> 01:00:01.002\n", result);
        Assert.Contains(text + " &lt;b&gt; &amp; --&gt;\nnext", result);
        Assert.DoesNotContain("machine", result);
        Assert.DoesNotContain("hidden", result);
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }));
    }

    [Fact]
    public async Task InvalidSubtitleContentIsExplicitlyRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new WebVttSubtitleService().GenerateWebVttAsync(
            [new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), TranslatedText = "bad\0" }],
            Path.Combine(directory, "bad.vtt"), None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WebVttSubtitleService().GenerateWebVttAsync(
            [new() { Start = TimeSpan.Zero, End = TimeSpan.FromTicks(100), TranslatedText = "Too short" }],
            Path.Combine(directory, "bad.vtt"), None));
    }

    [Theory]
    [InlineData("h264", "yuv420p", "copy")]
    [InlineData("hevc", "yuv420p", "libx264")]
    [InlineData("h264", "yuv444p", "libx264")]
    public async Task RendererReplacesAudioPreservesCompatiblePictureAndCreatesSoftSubtitles(string codec, string pixels, string encoder)
    {
        var runner = new Runner { SourceCodec = codec, SourcePixels = pixels };
        await Renderer(runner).RenderTranslatedVideoAsync(Path.Combine(directory, "original.mp4"), "audio.wav", "subtitles.vtt", None);
        var args = Assert.Single(runner.RenderCalls);
        Assert.Contains("0:v:0", args);
        Assert.Contains("1:a:0", args);
        Assert.Contains("2:s:0", args);
        Assert.Contains(encoder, args);
        Assert.Contains("mov_text", args);
        Assert.Contains("aac", args);
        Assert.Contains("+faststart", args);
        Assert.Contains("-noautorotate", args);
        Assert.DoesNotContain("0:a:0", args);
        Assert.DoesNotContain("-shortest", args);
        Assert.DoesNotContain(args, value => value.Contains("subtitles="));
    }

    [Theory]
    [InlineData(5.3, "h264", "aac", "mov_text")]
    [InlineData(5, "hevc", "aac", "mov_text")]
    [InlineData(5, "h264", "pcm_s16le", "mov_text")]
    [InlineData(5, "h264", "aac", "webvtt")]
    public async Task RendererRejectsWrongOutputDurationOrTracks(double seconds, string video, string audio, string subtitle)
    {
        var runner = new Runner { OutputDuration = seconds, OutputVideo = video, OutputAudio = audio, OutputSubtitle = subtitle };
        await Assert.ThrowsAsync<InvalidDataException>(() => Renderer(runner).RenderTranslatedVideoAsync(
            Path.Combine(directory, "original.mp4"), "audio.wav", "subtitles.vtt", None));
    }

    [Fact]
    public async Task RendererPreservesDisplayRotationAndRejectsOrientationChanges()
    {
        var runner = new Runner { SourceRotation = 90, OutputRotation = 90, SourceCodec = "mpeg4" };
        await Renderer(runner).RenderTranslatedVideoAsync(Path.Combine(directory, "original.mp4"), "audio.wav", "subtitles.vtt", None);
        await Assert.ThrowsAsync<InvalidDataException>(() => Renderer(new Runner { SourceRotation = 90 })
            .RenderTranslatedVideoAsync(Path.Combine(directory, "original.mp4"), "audio.wav", "subtitles.vtt", None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"format":{"duration":"NaN"},"streams":[]}""")]
    [InlineData("""{"format":{"duration":"5"},"streams":[null]}""")]
    public async Task RendererRejectsMalformedProbe(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Renderer(new Runner { Override = json })
            .RenderTranslatedVideoAsync(Path.Combine(directory, "original.mp4"), "audio.wav", "subtitles.vtt", None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateOutputStorageCommitsLastRejectsOverwriteAndDetectsCorruption(bool blob)
    {
        using var harness = new BlobStorageTests.BlobHarness();
        IRenderedVideoStorageService store = blob ? new BlobRenderedVideoStorageService(harness.Container) : Local();
        var job = Job();
        var request = job.ApprovedSpeech!.RequestId;
        var files = new Dictionary<RenderArtifact, RenderedFile>();
        foreach (var artifact in Enum.GetValues<RenderArtifact>())
        {
            var path = Path.Combine(directory, RenderedVideoMetadata.Name(artifact));
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await store.SaveArtifactAsync(job.JobId, request, artifact, path, None);
            files.Add(artifact, await RenderedVideoMetadata.InspectAsync(path, artifact, None));
        }
        Assert.Null(await store.GetResultAsync(job.JobId, request, None));
        var result = new RenderedVideoResult { RequestId = request, DurationSeconds = 5, Files = files };
        await store.CommitAsync(job.JobId, result, None);
        Assert.NotNull(await store.GetResultAsync(job.JobId, request, None));
        using var stream = new MemoryStream();
        await store.CopyAsync(job.JobId, request, RenderArtifact.Video, stream, None);
        await RenderedVideoMetadata.VerifyAsync(stream, files[RenderArtifact.Video], None);
        stream.Position = 0; stream.WriteByte(5);
        await Assert.ThrowsAsync<InvalidDataException>(() => RenderedVideoMetadata.VerifyAsync(stream, files[RenderArtifact.Video], None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveArtifactAsync(job.JobId, request,
            RenderArtifact.Video, Path.Combine(directory, "translated-video.mp4"), None));
        if (blob)
        {
            Assert.EndsWith("/result.json", harness.Handler.Writes.Last());
            harness.Handler.MissingContainer = true;
            await Assert.ThrowsAsync<JobStorageException>(() => store.GetResultAsync(job.JobId, request, None));
        }
    }

    [Fact]
    public async Task ProcessorCommitsBeforeCompletionAndRecoversWithoutRendering()
    {
        var job = Job();
        var state = new State(job);
        var runner = new Runner();
        var store = Local();
        await Processor(state, store, runner).ProcessAsync(state.Clone(), None);
        Assert.Equal(JobStatus.Completed, state.Job.Status);
        Assert.Equal(new[] { JobStatus.RenderingVideo, JobStatus.Completed }, state.Updates);
        Assert.NotNull(await store.GetResultAsync(job.JobId, job.ApprovedSpeech!.RequestId, None));
        var interrupted = JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(job))!;
        var recovery = new State(interrupted);
        await Processor(recovery, store, runner).ProcessAsync(recovery.Clone(), None);
        Assert.Equal(JobStatus.Completed, recovery.Job.Status);
        Assert.Single(runner.RenderCalls);
    }

    [Fact]
    public async Task ProcessorPersistsMediaFailuresAndLeavesStorageOrCancellationPending()
    {
        var state = new State(Job()) { SourceFailure = new UploadValidationException("Corrupt source.") };
        await Processor(state, Local(), new Runner()).ProcessAsync(state.Clone(), None);
        Assert.Equal(JobStatus.Failed, state.Job.Status);
        Assert.Equal(JobStatus.RenderingVideo, state.Job.FailedAtStatus);
        var pending = new State(Job()) { SourceFailure = new JobStorageException("Unavailable", new IOException()) };
        await Assert.ThrowsAsync<JobStorageException>(() => Processor(pending, Local(), new Runner()).ProcessAsync(pending.Clone(), None));
        Assert.Equal(JobStatus.RenderingVideo, pending.Job.Status);
        var cancelled = new State(Job()) { SourceFailure = new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => Processor(cancelled, Local(), new Runner()).ProcessAsync(cancelled.Clone(), None));
        Assert.Equal(JobStatus.RenderingVideo, cancelled.Job.Status);
    }

    [Fact]
    public async Task RenderingApprovalRequiresConfirmationAndRejectsDuplicateRequests()
    {
        var state = new State(Job(JobStatus.TranslatedAudioReady));
        var page = new TranslatedAudioModel(state, state, NullLogger<TranslatedAudioModel>.Instance);
        Assert.IsType<BadRequestObjectResult>(await page.OnPostRenderAsync(state.Job.JobId, false, None));
        Assert.Equal(JobStatus.TranslatedAudioReady, state.Job.Status);
        Assert.IsType<RedirectToPageResult>(await page.OnPostRenderAsync(state.Job.JobId, true, None));
        Assert.Equal(JobStatus.CreatingSubtitles, state.Job.Status);
        var duplicate = Assert.IsType<ObjectResult>(await page.OnPostRenderAsync(state.Job.JobId, true, None));
        Assert.Equal(409, duplicate.StatusCode);
    }

    [Fact]
    public async Task ResultDoesNotPretendMissingCommittedFilesAreSuccessful()
    {
        var state = new State(Job(JobStatus.Completed));
        var page = new ResultModel(state, Local(), NullLogger<ResultModel>.Instance)
        { PageContext = new PageContext { HttpContext = new DefaultHttpContext() } };
        var missing = Assert.IsType<ObjectResult>(await page.OnGetVideoAsync(state.Job.JobId, None));
        Assert.Equal(503, missing.StatusCode);
        Assert.IsType<NotFoundResult>(await page.OnGetAsync("invalid", None));
        var pending = new State(Job());
        var pendingPage = new ResultModel(pending, Local(), NullLogger<ResultModel>.Instance);
        Assert.IsType<RedirectToPageResult>(await pendingPage.OnGetAsync(pending.Job.JobId, None));
    }

    [Fact]
    public void CorruptSnapshotAndManifestIdentifiersAreDomainErrors()
    {
        var malformed = new SpeechGeneration { RequestId = "invalid" };
        Assert.Throws<InvalidDataException>(() => SpeechGenerationMetadata.Validate(malformed, "ar-SA"));
        Assert.Throws<InvalidDataException>(() => RenderedVideoMetadata.Validate(new(), "invalid"));
    }

    [Fact]
    public async Task ResultRejectsDamagedStoredFileRatherThanServingAValidLookingDownload()
    {
        var state = new State(Job());
        var store = Local();
        await Processor(state, store, new Runner()).ProcessAsync(state.Clone(), None);
        var file = Path.Combine(directory, "jobs", state.Job.JobId, "rendered",
            state.Job.ApprovedSpeech!.RequestId, "translated-video.mp4");
        await File.WriteAllBytesAsync(file, [5, 6, 7]);
        var page = new ResultModel(state, store, NullLogger<ResultModel>.Instance)
        { PageContext = new PageContext { HttpContext = new DefaultHttpContext() } };
        var result = Assert.IsType<ObjectResult>(await page.OnGetDownloadAsync(state.Job.JobId, None));
        Assert.Equal(503, result.StatusCode);
    }

    private IRenderedVideoStorageService Local() => new LocalRenderedVideoStorageService(
        new Environment { ContentRootPath = directory }, Options.Create(new LocalStorageOptions { RootPath = "jobs" }));
    private static FFmpegVideoRenderingService Renderer(Runner runner) => new(runner,
        new MediaAudioInspector(runner, Options.Create(new MediaOptions())), Options.Create(new MediaOptions()));
    private static VideoRenderingProcessor Processor(State state, IRenderedVideoStorageService store, Runner runner)
    {
        var renderer = Renderer(runner);
        return new(state, state, store, new WebVttSubtitleService(), renderer, renderer, NullLogger<VideoRenderingProcessor>.Instance);
    }
    private sealed class Runner : IMediaProcessRunner
    {
        public string SourceCodec { get; init; } = "h264";
        public string SourcePixels { get; init; } = "yuv420p";
        public double SourceRotation { get; init; }
        public double OutputRotation { get; init; }
        public double OutputDuration { get; init; } = 5;
        public string OutputVideo { get; init; } = "h264";
        public string OutputAudio { get; init; } = "aac";
        public string OutputSubtitle { get; init; } = "mov_text";
        public string? Override { get; init; }
        public List<IReadOnlyList<string>> RenderCalls { get; } = [];
        public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            if (!arguments.Contains("-show_entries"))
            {
                RenderCalls.Add(arguments);
                await File.WriteAllBytesAsync(arguments[^1], [1, 2, 3], token);
                return "";
            }
            if (Override is not null) { return Override; }
            var path = arguments[^1];
            if (path.EndsWith(".wav"))
            { return """{"format":{"duration":"5"},"streams":[{"codec_name":"pcm_s16le","sample_rate":"16000","channels":1,"bits_per_sample":16}]}"""; }
            var original = path.EndsWith("original.mp4");
            var streams = new List<object>
            {
                new { codec_type = "video", codec_name = original ? SourceCodec : OutputVideo,
                    pix_fmt = original ? SourcePixels : "yuv420p", width = 320, height = 240,
                    side_data_list = new[] { new { rotation = original ? SourceRotation : OutputRotation } } },
                new { codec_type = "audio", codec_name = OutputAudio }
            };
            if (!original) { streams.Add(new { codec_type = "subtitle", codec_name = OutputSubtitle }); }
            return JsonSerializer.Serialize(new { format = new { duration = (original ? 5 : OutputDuration).ToString(CultureInfo.InvariantCulture) }, streams });
        }
    }
    private sealed class State(VideoJob job) : IJobStorageService, IGeneratedAudioStorageService
    {
        public VideoJob Job { get; private set; } = job;
        public Exception? SourceFailure { get; init; }
        public List<JobStatus> Updates { get; } = [];
        public VideoJob Clone() => JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(Job))!;
        public Task<VideoJob?> GetAsync(string id, CancellationToken token) => Task.FromResult<VideoJob?>(Clone());
        public Task CreateAsync(VideoJob value, Stream stream, CancellationToken token) => throw new NotSupportedException();
        public IAsyncEnumerable<string> ListJobIdsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task DownloadVideoAsync(VideoJob value, string path, CancellationToken token) =>
            SourceFailure is not null ? Task.FromException(SourceFailure) : File.WriteAllBytesAsync(path, [1], token);
        public Task SaveAudioAsync(string id, string path, CancellationToken token) => throw new NotSupportedException();
        public Task UpdateAsync(VideoJob value, JobStatus expected, CancellationToken token)
        {
            Assert.Equal(expected, Job.Status); Job = JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(value))!;
            Updates.Add(value.Status); return Task.CompletedTask;
        }
        public Task<TimedAudioResult?> GetResultAsync(string id, string request, CancellationToken token) =>
            Task.FromResult<TimedAudioResult?>(new() { RequestId = request, DurationSeconds = 5, Segments = [new(1, 1, 1, 1), new(2, 0, 1, 1)] });
        public Task SaveAsync(string id, TimedAudioResult value, string path, CancellationToken token) => throw new NotSupportedException();
        public Task CopyAudioAsync(string id, string request, Stream stream, CancellationToken token) => stream.WriteAsync(VoicePreviewTests.Wave(), token).AsTask();
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "RenderingTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
