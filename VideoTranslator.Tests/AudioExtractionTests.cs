using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class AudioExtractionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-media-tests-{Guid.NewGuid():N}");
    private const string ValidProbe =
        """{"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"2.0"},"streams":[{"codec_type":"video"},{"codec_type":"audio"}]}""";

    public AudioExtractionTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ExtractionUsesSafeArgumentsAndSpeechCompatibleAudio()
    {
        var runner = new StubRunner { Probe = ValidProbe };
        var service = AudioService(runner);
        var video = Path.Combine(directory, "video with spaces.mp4");
        var result = await service.ExtractAudioAsync(video, directory, CancellationToken.None);

        Assert.Equal(Path.Combine(directory, "original-audio.wav"), result);
        Assert.Equal(3, runner.Calls.Count);
        Assert.Contains(video, runner.Calls[0]);
        Assert.Contains(video, runner.Calls[1]);
        AssertArguments(runner.Calls[1], "-ac", "1");
        AssertArguments(runner.Calls[1], "-ar", "16000");
        AssertArguments(runner.Calls[1], "-c:a", "pcm_s16le");
        AssertArguments(runner.Calls[0], "-protocol_whitelist", "file,pipe");
        AssertArguments(runner.Calls[1], "-protocol_whitelist", "file,pipe");
        AssertArguments(runner.Calls[1], "-threads", "1");
        Assert.Contains("-nostdin", runner.Calls[1]);
        Assert.Contains("-xerror", runner.Calls[1]);
    }

    [Theory]
    [InlineData("""{"format":{"format_name":"mp4","duration":"2"},"streams":[{"codec_type":"video"}]}""")]
    [InlineData("""{"format":{"format_name":"mp4","duration":"2"},"streams":[{"codec_type":"audio"}]}""")]
    [InlineData("""{"format":{"format_name":"mp4","duration":"0"},"streams":[{"codec_type":"video"},{"codec_type":"audio"}]}""")]
    [InlineData("""{"format":{"format_name":"mp4","duration":"NaN"},"streams":[]}""")]
    [InlineData("""{"format":{"format_name":"mp4","duration":"Infinity"},"streams":[]}""")]
    [InlineData("""{"format":{"format_name":"mpegts","duration":"2"},"streams":[]}""")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"format":null}""")]
    [InlineData("""{"format":{"format_name":"mp4","duration":"2"},"streams":[null,{"codec_type":4}]}""")]
    [InlineData("not-json")]
    public async Task InvalidMediaIsRejectedBeforeExtraction(string probe)
    {
        var runner = new StubRunner { Probe = probe };
        await Assert.ThrowsAsync<MediaProcessingException>(() =>
            AudioService(runner).ExtractAudioAsync("video.mp4", directory, CancellationToken.None));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task MissingAudioOutputIsNotSuccess()
    {
        var runner = new StubRunner { Probe = ValidProbe, WriteAudio = false };
        await Assert.ThrowsAsync<MediaProcessingException>(() =>
            AudioService(runner).ExtractAudioAsync("video.mp4", directory, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"format":{"duration":"0"},"streams":[]}""")]
    [InlineData("""{"format":{"duration":"2"},"streams":[{"codec_name":"mp3","sample_rate":"44100","channels":2,"bits_per_sample":16}]}""")]
    public async Task EmptyOrIncorrectAudioFormatIsRejected(string audioProbe)
    {
        var runner = new StubRunner { Probe = ValidProbe, AudioProbe = audioProbe };
        await Assert.ThrowsAsync<MediaProcessingException>(() =>
            AudioService(runner).ExtractAudioAsync("video.mp4", directory, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessorPersistsAudioBeforeReadyAndRecoversInterruptedJob(bool interrupted)
    {
        var job = new VideoJob { FileSizeBytes = 16 };
        if (interrupted)
        {
            job.TransitionTo(JobStatus.ExtractingAudio);
        }
        var storage = new StubStorage(job);
        var audio = new StubAudio();
        var processor = new AudioExtractionProcessor(storage, audio, NullLogger<AudioExtractionProcessor>.Instance);

        await processor.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobStatus.AudioReady, storage.Stored.Status);
        Assert.True(storage.AudioSaved);
        Assert.Equal(interrupted
            ? new[] { "download", "audio", "status:AudioReady" }
            : new[] { "status:ExtractingAudio", "download", "audio", "status:AudioReady" }, storage.Events);
        Assert.NotNull(audio.WorkingDirectory);
        Assert.False(Directory.Exists(audio.WorkingDirectory));
    }

    [Fact]
    public async Task InvalidMediaPersistsSafeFailureAndCleansDirectory()
    {
        var storage = new StubStorage(new VideoJob());
        var audio = new StubAudio { Failure = new MediaProcessingException("The MP4 must contain audio.") };
        await new AudioExtractionProcessor(storage, audio, NullLogger<AudioExtractionProcessor>.Instance)
            .ProcessAsync(storage.Clone(), CancellationToken.None);
        Assert.Equal(JobStatus.Failed, storage.Stored.Status);
        Assert.Equal("The MP4 must contain audio.", storage.Stored.ErrorMessage);
        Assert.NotNull(storage.Stored.CompletedUtc);
        Assert.False(storage.AudioSaved);
        Assert.False(Directory.Exists(audio.WorkingDirectory));
    }

    [Fact]
    public async Task ShutdownLeavesExtractionPendingForRestart()
    {
        var storage = new StubStorage(new VideoJob());
        var audio = new StubAudio { Failure = new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new AudioExtractionProcessor(storage, audio, NullLogger<AudioExtractionProcessor>.Instance)
                .ProcessAsync(storage.Clone(), CancellationToken.None));
        Assert.Equal(JobStatus.ExtractingAudio, storage.Stored.Status);
        Assert.Null(storage.Stored.ErrorMessage);
        Assert.False(Directory.Exists(audio.WorkingDirectory));
    }

    [Fact]
    public async Task StorageFailureDoesNotFalselyCompleteExtraction()
    {
        var storage = new StubStorage(new VideoJob())
        {
            AudioStorageFailure = true
        };
        var audio = new StubAudio();
        await Assert.ThrowsAsync<JobStorageException>(() =>
            new AudioExtractionProcessor(storage, audio, NullLogger<AudioExtractionProcessor>.Instance)
                .ProcessAsync(storage.Clone(), CancellationToken.None));
        Assert.Equal(JobStatus.ExtractingAudio, storage.Stored.Status);
        Assert.False(Directory.Exists(audio.WorkingDirectory));
    }

    [Fact]
    public async Task MissingExecutableGivesSafeError()
    {
        var runner = new MediaProcessRunner(Options.Create(new MediaOptions()),
            NullLogger<MediaProcessRunner>.Instance);
        var exception = await Assert.ThrowsAsync<MediaProcessingException>(() =>
            runner.RunAsync(Path.Combine(directory, "nonexistent-media-executable"), ["-version"], CancellationToken.None));
        Assert.DoesNotContain(directory, exception.Message);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task CancelledRunDoesNotStartProcess()
    {
        var runner = new MediaProcessRunner(Options.Create(new MediaOptions()),
            NullLogger<MediaProcessRunner>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("nonexistent-executable", [], cancellation.Token));
    }

    [Fact]
    public async Task WorkerProcessesJobsSequentiallyAndStopsAtAudioReady()
    {
        var storage = new WorkerStorage(new VideoJob { FileSizeBytes = 16 }, new VideoJob { FileSizeBytes = 16 });
        var audio = new StubAudio();
        var processor = new AudioExtractionProcessor(storage, audio, NullLogger<AudioExtractionProcessor>.Instance);
        using var worker = new AudioExtractionWorker(storage, processor,
            Options.Create(new MediaOptions { PollIntervalSeconds = 1 }),
            NullLogger<AudioExtractionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await storage.AllReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, audio.MaximumConcurrentCalls);
            Assert.Equal(2, audio.Calls);
            Assert.All(storage.Jobs.Values, item => Assert.Equal(JobStatus.AudioReady, item.Stored.Status));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static void AssertArguments(IReadOnlyList<string> args, string name, string value)
    {
        Assert.Contains(Enumerable.Range(0, args.Count - 1), index => args[index] == name && args[index + 1] == value);
    }

    private static FFmpegAudioService AudioService(StubRunner runner) => new(
        runner, Options.Create(new MediaOptions()), NullLogger<FFmpegAudioService>.Instance);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class StubRunner : IMediaProcessRunner
    {
        public string Probe { get; init; } = string.Empty;
        public string AudioProbe { get; init; } =
            """{"format":{"duration":"2"},"streams":[{"codec_name":"pcm_s16le","sample_rate":"16000","channels":1,"bits_per_sample":16}]}""";
        public bool WriteAudio { get; init; } = true;
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            if (executable == "ffprobe")
            {
                return Calls.Count == 1 ? Probe : AudioProbe;
            }
            if (WriteAudio)
            {
                await File.WriteAllBytesAsync(arguments[^1], new byte[128], cancellationToken);
            }
            return string.Empty;
        }
    }

    private sealed class StubAudio : IAudioService
    {
        private int active;
        public int Calls { get; private set; }
        public int MaximumConcurrentCalls { get; private set; }
        public Exception? Failure { get; init; }
        public string? WorkingDirectory { get; private set; }
        public async Task<string> ExtractAudioAsync(string videoPath, string workingDirectory, CancellationToken cancellationToken)
        {
            WorkingDirectory = workingDirectory;
            Calls++;
            var running = Interlocked.Increment(ref active);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, running);
            try
            {
                if (Failure is not null)
                {
                    throw Failure;
                }
                var path = Path.Combine(workingDirectory, "original-audio.wav");
                await Task.Delay(25, cancellationToken);
                await File.WriteAllBytesAsync(path, new byte[128], cancellationToken);
                return path;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private sealed class WorkerStorage(params VideoJob[] jobs) : IJobStorageService
    {
        public Dictionary<string, StubStorage> Jobs { get; } =
            jobs.ToDictionary(job => job.JobId, job => new StubStorage(job));
        public TaskCompletionSource AllReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CreateAsync(VideoJob job, Stream video, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoJob?> GetAsync(string jobId, CancellationToken cancellationToken) => Jobs[jobId].GetAsync(jobId, cancellationToken);
        public async IAsyncEnumerable<string> ListJobIdsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var id in Jobs.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return id;
            }
            await Task.CompletedTask;
        }
        public Task DownloadVideoAsync(VideoJob job, string destinationPath, CancellationToken cancellationToken) =>
            Jobs[job.JobId].DownloadVideoAsync(job, destinationPath, cancellationToken);
        public Task SaveAudioAsync(string jobId, string audioPath, CancellationToken cancellationToken) =>
            Jobs[jobId].SaveAudioAsync(jobId, audioPath, cancellationToken);
        public async Task UpdateAsync(VideoJob job, JobStatus expectedStatus, CancellationToken cancellationToken)
        {
            await Jobs[job.JobId].UpdateAsync(job, expectedStatus, cancellationToken);
            if (Jobs.Values.All(item => item.Stored.Status == JobStatus.AudioReady))
            {
                AllReady.TrySetResult();
            }
        }
    }

    private sealed class StubStorage(VideoJob job) : IJobStorageService
    {
        public VideoJob Stored { get; private set; } = JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(job))!;
        public List<string> Events { get; } = [];
        public bool AudioSaved { get; private set; }
        public bool AudioStorageFailure { get; init; }
        public VideoJob Clone() => JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(Stored))!;
        public Task CreateAsync(VideoJob job, Stream video, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoJob?> GetAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult<VideoJob?>(Clone());
        public async IAsyncEnumerable<string> ListJobIdsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Stored.JobId;
            await Task.CompletedTask;
        }
        public async Task DownloadVideoAsync(VideoJob job, string destinationPath, CancellationToken cancellationToken)
        {
            Events.Add("download");
            await File.WriteAllBytesAsync(destinationPath, UploadValidatorTests.Mp4Header(), cancellationToken);
        }
        public Task SaveAudioAsync(string jobId, string audioPath, CancellationToken cancellationToken)
        {
            if (AudioStorageFailure)
            {
                throw new JobStorageException("Storage unavailable.", new IOException());
            }
            Assert.True(File.Exists(audioPath));
            Events.Add("audio");
            AudioSaved = true;
            return Task.CompletedTask;
        }
        public Task UpdateAsync(VideoJob job, JobStatus expectedStatus, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedStatus, Stored.Status);
            Stored = JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(job))!;
            Events.Add($"status:{job.Status}");
            return Task.CompletedTask;
        }
    }
}
