using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class BlobStorageTests
{
    [Fact]
    public async Task UploadPublishesMetadataLastAndRoundTripsJob()
    {
        using var harness = new BlobHarness();
        var job = new VideoJob
        {
            OriginalFileName = "sample.mp4", FileSizeBytes = 16, SelectedLanguage = "ar-SA"
        };
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());

        await harness.Storage.CreateAsync(job, stream, CancellationToken.None);
        var restored = await harness.Storage.GetAsync(job.JobId, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(job.JobId, restored.JobId);
        Assert.Equal(job.OriginalFileName, restored.OriginalFileName);
        Assert.Equal(job.SelectedLanguage, restored.SelectedLanguage);
        Assert.Equal(job.FileSizeBytes, restored.FileSizeBytes);
        Assert.Equal(JobStatus.Uploaded, restored.Status);
        Assert.Equal(job.CreatedUtc, restored.CreatedUtc);
        Assert.Equal(new[]
        {
            $"/jobs/jobs/{job.JobId}/original/original-video.mp4",
            $"/jobs/jobs/{job.JobId}/job.json"
        }, harness.Handler.Writes);
        Assert.All(harness.Handler.CreateConditions, condition => Assert.Equal("*", condition));
    }

    [Fact]
    public async Task ValidationRejectsContentBeforeAnyAzureRequest()
    {
        using var harness = new BlobHarness();
        await using var stream = new MemoryStream(new byte[16]);
        await Assert.ThrowsAsync<UploadValidationException>(() =>
            harness.Storage.CreateAsync(new VideoJob { FileSizeBytes = 16 }, stream, CancellationToken.None));
        Assert.Empty(harness.Handler.Writes);
        Assert.Empty(harness.Handler.Blobs);
    }

    [Fact]
    public async Task MetadataFailureRemovesOnlyOriginalForThatJob()
    {
        using var harness = new BlobHarness();
        harness.Handler.FailMetadata = true;
        harness.Handler.Blobs["/jobs/unrelated.mp4"] = [1, 2, 3];
        var job = new VideoJob { FileSizeBytes = 16 };
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());

        var exception = await Assert.ThrowsAsync<JobStorageException>(() =>
            harness.Storage.CreateAsync(job, stream, CancellationToken.None));

        Assert.IsType<RequestFailedException>(exception.InnerException);
        Assert.Single(harness.Handler.Blobs);
        Assert.True(harness.Handler.Blobs.ContainsKey("/jobs/unrelated.mp4"));
        Assert.Equal($"/jobs/jobs/{job.JobId}/original/original-video.mp4", Assert.Single(harness.Handler.Deletes));
    }

    [Fact]
    public async Task AmbiguousMetadataCommitPreservesOriginal()
    {
        using var harness = new BlobHarness();
        harness.Handler.FailMetadata = true;
        harness.Handler.CommitMetadataBeforeError = true;
        var job = new VideoJob { FileSizeBytes = 16 };
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());
        await Assert.ThrowsAsync<JobStorageException>(() =>
            harness.Storage.CreateAsync(job, stream, CancellationToken.None));
        Assert.Equal(2, harness.Handler.Blobs.Count);
        Assert.Empty(harness.Handler.Deletes);
        Assert.NotNull(await harness.Storage.GetAsync(job.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task ExistingOriginalIsNeverOverwrittenOrDeleted()
    {
        using var harness = new BlobHarness();
        var job = new VideoJob { FileSizeBytes = 16 };
        var path = $"/jobs/jobs/{job.JobId}/original/original-video.mp4";
        harness.Handler.Blobs[path] = [4, 5, 6];
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());
        await Assert.ThrowsAsync<JobStorageException>(() =>
            harness.Storage.CreateAsync(job, stream, CancellationToken.None));
        Assert.Equal(new byte[] { 4, 5, 6 }, harness.Handler.Blobs[path]);
        Assert.Empty(harness.Handler.Deletes);
    }

    [Fact]
    public async Task OnlyMissingBlobIsReportedAsMissingJob()
    {
        using var harness = new BlobHarness();
        var id = Guid.NewGuid().ToString("N");
        Assert.Null(await harness.Storage.GetAsync(id, CancellationToken.None));

        harness.Handler.MissingContainer = true;
        await Assert.ThrowsAsync<JobStorageException>(() => harness.Storage.GetAsync(id, CancellationToken.None));
        harness.Handler.MissingContainer = false;
        harness.Handler.DenyReads = true;
        await Assert.ThrowsAsync<JobStorageException>(() => harness.Storage.GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidJobIdAndCorruptMetadataAreNotHidden()
    {
        using var harness = new BlobHarness();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Storage.GetAsync("../outside", CancellationToken.None));
        var id = Guid.NewGuid().ToString("N");
        harness.Handler.Blobs[$"/jobs/jobs/{id}/job.json"] = Encoding.UTF8.GetBytes("invalid");
        await Assert.ThrowsAsync<JsonException>(() => harness.Storage.GetAsync(id, CancellationToken.None));
        harness.Handler.Blobs[$"/jobs/jobs/{id}/job.json"] =
            JsonSerializer.SerializeToUtf8Bytes(new VideoJob());
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Storage.GetAsync(id, CancellationToken.None));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("blob", false)]
    [InlineData("container", false)]
    public async Task StartupRequiresPrivateContainer(string? publicAccess, bool allowed)
    {
        using var harness = new BlobHarness();
        harness.Handler.PublicAccess = publicAccess;
        var check = new BlobStorageStartupCheck(harness.Container, NullLogger<BlobStorageStartupCheck>.Instance);
        if (allowed)
        {
            await check.StartAsync(CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task BlobStorageSupportsDownloadAudioAndConditionalMetadataUpdate()
    {
        using var harness = new BlobHarness();
        var job = new VideoJob { FileSizeBytes = 16 };
        await using var stream = new MemoryStream(UploadValidatorTests.Mp4Header());
        await harness.Storage.CreateAsync(job, stream, CancellationToken.None);
        var directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-blob-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var videoPath = Path.Combine(directory, "original.mp4");
            await harness.Storage.DownloadVideoAsync(job, videoPath, CancellationToken.None);
            Assert.Equal(UploadValidatorTests.Mp4Header(), await File.ReadAllBytesAsync(videoPath));
            job.TransitionTo(JobStatus.ExtractingAudio);
            await harness.Storage.UpdateAsync(job, JobStatus.Uploaded, CancellationToken.None);
            Assert.Equal("\"test-etag\"", harness.Handler.UpdateConditions.Single());
            await Assert.ThrowsAsync<JobStorageException>(() =>
                harness.Storage.UpdateAsync(job, JobStatus.Uploaded, CancellationToken.None));
            var audioPath = Path.Combine(directory, "audio.wav");
            await File.WriteAllBytesAsync(audioPath, new byte[128]);
            await harness.Storage.SaveAudioAsync(job.JobId, audioPath, CancellationToken.None);
            var downloadedAudio = Path.Combine(directory, "downloaded.wav");
            await harness.Storage.DownloadAudioAsync(job.JobId, downloadedAudio, CancellationToken.None);
            Assert.Equal(128, new FileInfo(downloadedAudio).Length);
            Assert.Null(await harness.Storage.GetTranscriptAsync(job.JobId, CancellationToken.None));
            var transcript = new Transcript
            {
                SourceLanguage = "en-NZ",
                Segments = [new VideoSegment { Sequence = 1, End = TimeSpan.FromSeconds(1), OriginalText = "Hello." }]
            };
            await harness.Storage.SaveTranscriptAsync(job.JobId, transcript, CancellationToken.None);
            var restoredTranscript = await harness.Storage.GetTranscriptAsync(job.JobId, CancellationToken.None);
            Assert.NotNull(restoredTranscript);
            Assert.Equal("Hello.", Assert.Single(restoredTranscript.Segments).OriginalText);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                harness.Storage.SaveTranscriptAsync(job.JobId, new Transcript(), CancellationToken.None));
            harness.Handler.MissingContainer = true;
            await Assert.ThrowsAsync<JobStorageException>(() =>
                harness.Storage.GetTranscriptAsync(job.JobId, CancellationToken.None));
            harness.Handler.MissingContainer = false;
            job.TransitionTo(JobStatus.AudioReady);
            await harness.Storage.UpdateAsync(job, JobStatus.ExtractingAudio, CancellationToken.None);
            Assert.Equal(JobStatus.AudioReady, (await harness.Storage.GetAsync(job.JobId, CancellationToken.None))!.Status);
            Assert.Equal(128, harness.Handler.Blobs[$"/jobs/jobs/{job.JobId}/working/original-audio.wav"].Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JobListingIncludesOnlyPublishedMetadataWithValidIds()
    {
        using var harness = new BlobHarness();
        var id = Guid.NewGuid().ToString("N");
        harness.Handler.Blobs[$"/jobs/jobs/{id}/job.json"] = [];
        harness.Handler.Blobs[$"/jobs/jobs/{id}/working/original-audio.wav"] = [];
        harness.Handler.Blobs["/jobs/jobs/not-a-job/job.json"] = [];
        harness.Handler.Blobs["/jobs/jobs/deeper/nested/job.json"] = [];
        var ids = new List<string>();
        await foreach (var jobId in harness.Storage.ListJobIdsAsync(CancellationToken.None))
        {
            ids.Add(jobId);
        }
        Assert.Equal(id, Assert.Single(ids));
        harness.Handler.MissingContainer = true;
        await Assert.ThrowsAsync<JobStorageException>(async () =>
        {
            await foreach (var jobId in harness.Storage.ListJobIdsAsync(CancellationToken.None))
            {
                Assert.Fail($"Unexpected job {jobId}");
            }
        });
    }

    internal sealed class BlobHarness : IDisposable
    {
        public StorageHandler Handler { get; } = new();
        private readonly HttpClient client;
        public BlobContainerClient Container { get; }
        public BlobJobStorageService Storage { get; }

        public BlobHarness()
        {
            client = new HttpClient(Handler);
            var clientOptions = new BlobClientOptions { Transport = new HttpClientTransport(client) };
            clientOptions.Retry.MaxRetries = 0;
            Container = new BlobContainerClient(new Uri("https://storage.example.test/jobs"),
                new AzureSasCredential("sig=test-fixture"), clientOptions);
            Storage = new BlobJobStorageService(Container, Options.Create(new UploadOptions()),
                new UploadValidator(Options.Create(new UploadOptions())),
                NullLogger<BlobJobStorageService>.Instance);
        }

        public void Dispose() => client.Dispose();
    }

    internal sealed class StorageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> revisions = [];
        public Dictionary<string, byte[]> Blobs { get; } = [];
        public List<string> Writes { get; } = [];
        public List<string> Deletes { get; } = [];
        public List<string> CreateConditions { get; } = [];
        public List<string> UpdateConditions { get; } = [];
        public bool FailMetadata { get; set; }
        public bool CommitMetadataBeforeError { get; set; }
        public bool DenyReads { get; set; }
        public bool MissingContainer { get; set; }
        public string? PublicAccess { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (MissingContainer)
            {
                return Error(HttpStatusCode.NotFound, "ContainerNotFound");
            }

            if (uri.Query.Contains("restype=container", StringComparison.Ordinal))
            {
                if (uri.Query.Contains("comp=list", StringComparison.Ordinal))
                {
                    var blobs = string.Join("", Blobs.Keys.Select(name =>
                        $"<Blob><Name>{name["/jobs/".Length..]}</Name><Properties><Content-Length>16</Content-Length><BlobType>BlockBlob</BlobType></Properties></Blob>"));
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            $"<EnumerationResults ServiceEndpoint=\"https://storage.example.test/\" ContainerName=\"jobs\"><Blobs>{blobs}</Blobs><NextMarker /></EnumerationResults>",
                            Encoding.UTF8, "application/xml")
                    };
                }
                var response = Success(HttpStatusCode.OK, []);
                if (PublicAccess is not null)
                {
                    response.Headers.Add("x-ms-blob-public-access", PublicAccess);
                }
                return response;
            }

            if (request.Method == HttpMethod.Put)
            {
                Writes.Add(path);
                var condition = request.Headers.TryGetValues("If-None-Match", out var createValues)
                    ? createValues.Single() : string.Empty;
                if (condition.Length > 0)
                {
                    CreateConditions.Add(condition);
                }
                if (request.Headers.TryGetValues("If-Match", out var updateValues))
                {
                    var revision = updateValues.Single();
                    UpdateConditions.Add(revision);
                    if (!Blobs.ContainsKey(path) || revision != Revision(path))
                    {
                        return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                    }
                }
                if (condition == "*" && Blobs.ContainsKey(path))
                {
                    return Error(HttpStatusCode.PreconditionFailed, "BlobAlreadyExists");
                }
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                if (FailMetadata && path.EndsWith("/job.json", StringComparison.Ordinal))
                {
                    if (CommitMetadataBeforeError)
                    {
                        Blobs[path] = bytes;
                    }
                    return Error(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch");
                }
                Blobs[path] = bytes;
                if (path.EndsWith("/edited-script.json", StringComparison.Ordinal))
                {
                    revisions[path] = $"\"{Guid.NewGuid():N}\"";
                }
                return Success(HttpStatusCode.Created, [], Revision(path));
            }

            if (request.Method == HttpMethod.Delete)
            {
                Deletes.Add(path);
                Blobs.Remove(path);
                return Success(HttpStatusCode.Accepted, []);
            }

            if (DenyReads)
            {
                return Error(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch");
            }
            return Blobs.TryGetValue(path, out var content)
                ? Success(HttpStatusCode.OK, content, Revision(path))
                : Error(HttpStatusCode.NotFound, "BlobNotFound");
        }

        private string Revision(string path) => revisions.GetValueOrDefault(path, "\"test-etag\"");

        private static HttpResponseMessage Success(HttpStatusCode status, byte[] content, string revision = "\"test-etag\"")
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(content) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(revision);
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            response.Content.Headers.ContentLength = content.Length;
            response.Headers.Add("x-ms-request-id", "test-request");
            return response;
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent($"<Error><Code>{code}</Code><Message>Test storage failure.</Message></Error>",
                    Encoding.UTF8, "application/xml")
            };
            response.Headers.Add("x-ms-error-code", code);
            return response;
        }
    }
}
