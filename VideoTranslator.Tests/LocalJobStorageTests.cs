using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class LocalJobStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-tests-{Guid.NewGuid():N}");
    private readonly TestEnvironment environment;
    private readonly LocalJobStorageService storage;

    public LocalJobStorageTests()
    {
        environment = new TestEnvironment { ContentRootPath = directory };
        storage = CreateStorage();
    }

    [Fact]
    public async Task UploadPersistsAcrossServiceInstancesWithoutUsingBrowserFilenameAsPath()
    {
        var videoService = new VideoService(
            new LanguageService(Options.Create(new LanguageOptions
            {
                Supported = [LanguageTests.Language("hi-IN")]
            })),
            new UploadValidator(Options.Create(new UploadOptions())),
            storage, NullLogger<VideoService>.Instance);
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());
        var job = await videoService.UploadAsync(stream, "../../sample.mp4", "video/mp4",
            stream.Length, "hi-IN", CancellationToken.None);

        var restored = await CreateStorage().GetAsync(job.JobId, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("sample.mp4", restored.OriginalFileName);
        Assert.Equal("hi-IN", restored.SelectedLanguage);
        Assert.Equal(JobStatus.Uploaded, restored.Status);
        Assert.Equal(job.CreatedUtc, restored.CreatedUtc);
        Assert.Equal(stream.Length, restored.FileSizeBytes);
        Assert.True(Guid.TryParseExact(restored.JobId, "N", out _));
        var jobDirectory = Path.Combine(directory, "Data", "jobs", job.JobId);
        Assert.True(File.Exists(Path.Combine(jobDirectory, "original-video.mp4")));
        Assert.True(File.Exists(Path.Combine(jobDirectory, "job.json")));
        Assert.Single(Directory.GetDirectories(Path.Combine(directory, "Data", "jobs")));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public async Task SizeMismatchDoesNotPublishJobAndCleansStaging(long declaredLength)
    {
        var job = new VideoJob { FileSizeBytes = declaredLength };
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());

        await Assert.ThrowsAsync<UploadValidationException>(() =>
            storage.CreateAsync(job, stream, CancellationToken.None));

        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "Data", "jobs")));
        Assert.Null(await storage.GetAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidContentCleansUpStaging()
    {
        await using var stream = new MemoryStream(new byte[16]);
        await Assert.ThrowsAsync<UploadValidationException>(() =>
            storage.CreateAsync(new VideoJob { FileSizeBytes = 16 }, stream, CancellationToken.None));
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "Data", "jobs")));
    }

    [Fact]
    public async Task CopyEnforcesConfiguredSizeLimitEvenIfDeclaredSizeIsLarger()
    {
        var uploadOptions = Options.Create(new UploadOptions { MaxFileSizeBytes = 12 });
        var limitedStorage = new LocalJobStorageService(
            environment, Options.Create(new LocalStorageOptions()), uploadOptions,
            new UploadValidator(uploadOptions), NullLogger<LocalJobStorageService>.Instance);
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());
        await Assert.ThrowsAsync<UploadValidationException>(() =>
            limitedStorage.CreateAsync(new VideoJob { FileSizeBytes = 16 }, stream, CancellationToken.None));
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "Data", "jobs")));
    }

    [Fact]
    public async Task UnsupportedLanguageIsRejectedBeforeStorageIsCreated()
    {
        var videoService = new VideoService(
            new LanguageService(Options.Create(new LanguageOptions
            {
                Supported = [LanguageTests.Language("hi-IN")]
            })),
            new UploadValidator(Options.Create(new UploadOptions())),
            storage, NullLogger<VideoService>.Instance);
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());
        await Assert.ThrowsAsync<UploadValidationException>(() =>
            videoService.UploadAsync(stream, "sample.mp4", "video/mp4", stream.Length,
                "unsupported", CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(directory, "Data", "jobs")));
    }

    [Fact]
    public async Task CancellationCleansUpStaging()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            storage.CreateAsync(new VideoJob { FileSizeBytes = 16 }, stream, cancellation.Token));
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "Data", "jobs")));
    }

    [Fact]
    public async Task StorageRejectsTraversalAndReturnsNullForMissingValidId()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => storage.GetAsync("../job", CancellationToken.None));
        Assert.Null(await storage.GetAsync(Guid.NewGuid().ToString("N"), CancellationToken.None));
    }

    [Fact]
    public void PublicWebRootAndProductionLocalStorageAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CreateStorage("wwwroot/uploads"));
        environment.EnvironmentName = Environments.Production;
        Assert.Throws<InvalidOperationException>(() => CreateStorage());
    }

    [Fact]
    public async Task CorruptMetadataIsNotReportedAsMissingJob()
    {
        var id = Guid.NewGuid().ToString("N");
        var jobDirectory = Path.Combine(directory, "Data", "jobs", id);
        Directory.CreateDirectory(jobDirectory);
        await File.WriteAllTextAsync(Path.Combine(jobDirectory, "job.json"), "invalid");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => storage.GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task LocalStorageSupportsWorkerLifecycleAndRejectsStaleStatusUpdates()
    {
        var job = new VideoJob { FileSizeBytes = 16 };
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());
        await storage.CreateAsync(job, stream, CancellationToken.None);
        var ids = new List<string>();
        await foreach (var id in storage.ListJobIdsAsync(CancellationToken.None))
        {
            ids.Add(id);
        }
        Assert.Equal(job.JobId, Assert.Single(ids));
        var downloadPath = Path.Combine(directory, "download.mp4");
        await storage.DownloadVideoAsync(job, downloadPath, CancellationToken.None);
        Assert.Equal(UploadValidatorTests.Mp4Header(), await File.ReadAllBytesAsync(downloadPath));
        job.TransitionTo(JobStatus.ExtractingAudio);
        await storage.UpdateAsync(job, JobStatus.Uploaded, CancellationToken.None);
        await Assert.ThrowsAsync<JobStorageException>(() =>
            storage.UpdateAsync(job, JobStatus.Uploaded, CancellationToken.None));
        var audioPath = Path.Combine(directory, "audio.wav");
        await File.WriteAllBytesAsync(audioPath, new byte[128]);
        await storage.SaveAudioAsync(job.JobId, audioPath, CancellationToken.None);
        var audioDownload = Path.Combine(directory, "download.wav");
        await storage.DownloadAudioAsync(job.JobId, audioDownload, CancellationToken.None);
        Assert.Equal(128, new FileInfo(audioDownload).Length);
        Assert.Null(await storage.GetTranscriptAsync(job.JobId, CancellationToken.None));
        var transcript = new Transcript
        {
            SourceLanguage = "en-NZ",
            Segments =
            [
                new VideoSegment { Sequence = 1, Start = TimeSpan.Zero,
                    End = TimeSpan.FromSeconds(1), OriginalText = "Hello world." }
            ]
        };
        await storage.SaveTranscriptAsync(job.JobId, transcript, CancellationToken.None);
        var restoredTranscript = await storage.GetTranscriptAsync(job.JobId, CancellationToken.None);
        Assert.NotNull(restoredTranscript);
        Assert.Equal("en-NZ", restoredTranscript.SourceLanguage);
        Assert.Equal("Hello world.", Assert.Single(restoredTranscript.Segments).OriginalText);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.SaveTranscriptAsync(job.JobId, new Transcript(), CancellationToken.None));
        job.TransitionTo(JobStatus.AudioReady);
        await storage.UpdateAsync(job, JobStatus.ExtractingAudio, CancellationToken.None);
        Assert.Equal(JobStatus.AudioReady, (await storage.GetAsync(job.JobId, CancellationToken.None))!.Status);
        Assert.Equal(128, new FileInfo(Path.Combine(directory, "Data", "jobs", job.JobId, "original-audio.wav")).Length);
    }

    private LocalJobStorageService CreateStorage(string root = "Data/jobs") => new(
        environment, Options.Create(new LocalStorageOptions { RootPath = root }),
        Options.Create(new UploadOptions()), new UploadValidator(Options.Create(new UploadOptions())),
        NullLogger<LocalJobStorageService>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "VideoTranslator.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
