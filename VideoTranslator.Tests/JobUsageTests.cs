using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class JobUsageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"VideoTranslator-usage-{Guid.NewGuid():N}");
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly string JobId = Guid.NewGuid().ToString("N");
    private static JobUsageService Service(IJobUsageStorage storage, JobCostOptions? rates = null) =>
        new(storage, Options.Create(rates ?? new JobCostOptions()));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsageSurvivesRestartAndPendingRequestsAreNotShownAsFree(bool blob)
    {
        using var harness = new BlobStorageTests.BlobHarness();
        IJobUsageStorage storage = blob ? new BlobJobUsageStorage(harness.Container) : new LocalJobUsageStorage(root);
        var service = Service(storage);
        var pending = await service.StartAsync(JobId, "OpenAI", None);
        var start = await service.StartAsync(JobId, "Transcription", None);
        var before = await service.GetAsync(JobId, None);
        Assert.Equal(2, before.UnresolvedRequests);
        await service.CompleteAsync(JobId, start, 3600, 0, 0, 0, 0, None);
        var restarted = Service(blob ? new BlobJobUsageStorage(harness.Container) : new LocalJobUsageStorage(root));
        var after = await restarted.GetAsync(JobId, None);
        Assert.Equal(0.6360m, after.KnownNzd);
        Assert.Equal(1, after.UnresolvedRequests);
        Assert.Equal(start.RequestId, Assert.Single(after.CompletedRequests).RequestId);
        Assert.NotEqual(pending.RequestId, start.RequestId);
        Assert.Equal("2026-10-05", after.CompletedRequests[0].RateDate);
        await Assert.ThrowsAsync<JobStorageException>(() =>
            service.CompleteAsync(JobId, start, 3600, 0, 0, 0, 0, None));
    }

    [Fact]
    public async Task RatesAreSnapshottedAndRetriesSumWithCachedInputDiscount()
    {
        var store = new LocalJobUsageStorage(root);
        var service = Service(store);
        var first = await service.StartAsync(JobId, "OpenAI", None);
        await service.CompleteAsync(JobId, first, 0, 0, 2000, 1000, 1000, None);
        var synthesis = await service.StartAsync(JobId, "Synthesis", None);
        await service.CompleteAsync(JobId, synthesis, 0, 1000, 0, 0, 0, None);
        var changed = Service(store, new JobCostOptions { TranscriptionPerHour = 99, InputPerThousandTokens = 99 });
        var summary = await changed.GetAsync(JobId, None);
        Assert.Equal(0.0049m + 0.0024m + 0.0194m + 0.0264994m, summary.KnownNzd);
        var retry = await service.StartAsync(JobId, "Synthesis", None);
        await service.CompleteAsync(JobId, retry, 0, 1000, 0, 0, 0, None);
        Assert.Equal(summary.KnownNzd + 0.0264994m, (await service.GetAsync(JobId, None)).KnownNzd);
    }

    [Fact]
    public async Task EmptyHistoryHasNoInventedRequests()
    {
        var summary = await Service(new LocalJobUsageStorage(root)).GetAsync(JobId, None);
        Assert.Empty(summary.CompletedRequests);
        Assert.Equal(0, summary.UnresolvedRequests);
    }

    [Fact]
    public async Task InvalidUsageAndUnreadableStorageAreExplicit()
    {
        using var harness = new BlobStorageTests.BlobHarness();
        var service = Service(new BlobJobUsageStorage(harness.Container));
        var start = await service.StartAsync(JobId, "OpenAI", None);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CompleteAsync(JobId, start, 0, 0, 1, 2, 1, None));
        harness.Handler.DenyReads = true;
        await Assert.ThrowsAsync<JobStorageException>(() => service.GetAsync(JobId, None));
        harness.Handler.DenyReads = false;
        harness.Handler.MissingContainer = true;
        await Assert.ThrowsAsync<JobStorageException>(() => service.StartAsync(JobId, "Synthesis", None));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
