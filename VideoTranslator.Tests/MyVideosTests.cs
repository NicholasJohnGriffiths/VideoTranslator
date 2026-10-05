using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using VideoTranslator.Models;
using VideoTranslator.Pages;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class MyVideosTests
{
    private static MyVideosModel Model(Storage storage) => new(
        storage, new Languages(), NullLogger<MyVideosModel>.Instance)
    { PageContext = new PageContext { HttpContext = new DefaultHttpContext() } };

    [Fact]
    public async Task EmptyHistoryDisplaysEmptyPage()
    {
        var model = Model(new Storage());
        Assert.IsType<PageResult>(await model.OnGetAsync());
        Assert.Empty(model.Jobs);
        Assert.Equal(0, model.TotalCount);
        Assert.Equal(1, model.TotalPages);
        Assert.Equal(1, model.PageNumber);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task HistoryIsNewestFirstAndPaginatedDeterministically()
    {
        var jobs = Enumerable.Range(0, 25).Select(index => new VideoJob
        {
            OriginalFileName = $"{index}.mp4", SelectedLanguage = "es-ES",
            CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index)
        }).ToArray();
        var model = Model(new Storage { Jobs = jobs });
        await model.OnGetAsync();
        Assert.Equal(25, model.TotalCount);
        Assert.Equal(20, model.Jobs.Count);
        Assert.Equal("24.mp4", model.Jobs[0].OriginalFileName);
        Assert.Equal("5.mp4", model.Jobs[^1].OriginalFileName);
        Assert.Equal("Spanish", model.LanguageName(model.Jobs[0]));
        await model.OnGetAsync(2);
        Assert.Equal(5, model.Jobs.Count);
        Assert.Equal("4.mp4", model.Jobs[0].OriginalFileName);
        await model.OnGetAsync(int.MaxValue);
        Assert.Equal(2, model.PageNumber);
    }

    [Fact]
    public async Task SameDatesUseStableJobIdentifierOrder()
    {
        var date = DateTime.UtcNow;
        var jobs = new[] { new VideoJob { JobId = "b", CreatedUtc = date }, new VideoJob { JobId = "a", CreatedUtc = date } };
        var model = Model(new Storage { Jobs = jobs });
        await model.OnGetAsync();
        Assert.Equal(new[] { "a", "b" }, model.Jobs.Select(job => job.JobId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidPageNumberIsRejected(int number) =>
        Assert.IsType<BadRequestObjectResult>(await Model(new Storage()).OnGetAsync(number));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageFailureIsExplicitAndNeverShowsPartialSuccess(bool enumeration)
    {
        var model = Model(new Storage { EnumerationFailure = enumeration, ReadFailure = !enumeration });
        Assert.IsType<PageResult>(await model.OnGetAsync());
        Assert.Equal(503, model.Response.StatusCode);
        Assert.NotNull(model.ErrorMessage);
        Assert.Empty(model.Jobs);
    }

    [Fact]
    public async Task MissingJobsAreIgnoredButCorruptMetadataIsReported()
    {
        var model = Model(new Storage { MissingJob = true, CorruptJob = true, Jobs = [new VideoJob()] });
        await model.OnGetAsync();
        Assert.Single(model.Jobs);
        Assert.Equal(1, model.UnreadableCount);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Model(new Storage()).OnGetAsync(cancellationToken: cancellation.Token));
    }

    [Fact]
    public void EveryKnownStatusHasReadableLabelAndUnknownLanguageIsPreserved()
    {
        foreach (var status in Enum.GetValues<JobStatus>())
        {
            Assert.False(string.IsNullOrWhiteSpace(MyVideosModel.StatusLabel(status)));
        }
        Assert.Equal("Script review needed", MyVideosModel.StatusLabel(JobStatus.AwaitingScriptReview));
        Assert.Equal("Completed", MyVideosModel.StatusLabel(JobStatus.Completed));
        Assert.Equal("custom", Model(new Storage()).LanguageName(new VideoJob { SelectedLanguage = "custom" }));
    }

    private sealed class Languages : ILanguageService
    {
        public IReadOnlyList<LanguageOption> GetLanguages() => [new() { Code = "es-ES", DisplayName = "Spanish" }];
        public LanguageOption? Find(string code) => GetLanguages().FirstOrDefault(language => language.Code == code);
    }

    private sealed class Storage : IJobStorageService
    {
        public VideoJob[] Jobs { get; init; } = [];
        public bool EnumerationFailure { get; init; }
        public bool ReadFailure { get; init; }
        public bool MissingJob { get; init; }
        public bool CorruptJob { get; init; }
        public async IAsyncEnumerable<string> ListJobIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            if (EnumerationFailure) { throw new JobStorageException("Test storage failure.", new IOException()); }
            if (ReadFailure) { yield return "unavailable"; }
            if (MissingJob) { yield return "missing"; }
            if (CorruptJob) { yield return "corrupt"; }
            foreach (var job in Jobs) { yield return job.JobId; }
        }
        public Task<VideoJob?> GetAsync(string id, CancellationToken cancellationToken)
        {
            if (ReadFailure) { throw new JobStorageException("Test storage failure.", new IOException()); }
            if (id == "corrupt") { throw new JsonException("Test corrupt metadata."); }
            return Task.FromResult(Jobs.FirstOrDefault(job => job.JobId == id));
        }
        public Task CreateAsync(VideoJob job, Stream video, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DownloadVideoAsync(VideoJob job, string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAudioAsync(string id, string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateAsync(VideoJob job, JobStatus status, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
