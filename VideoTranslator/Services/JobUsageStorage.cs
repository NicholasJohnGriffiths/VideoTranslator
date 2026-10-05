using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface IJobUsageStorage
{
    Task AppendAsync(string jobId, JobUsage usage, CancellationToken token);
    Task<IReadOnlyList<JobUsage>> ReadAsync(string jobId, CancellationToken token);
}

public sealed class BlobJobUsageStorage(BlobContainerClient container) : IJobUsageStorage
{
    public async Task AppendAsync(string jobId, JobUsage usage, CancellationToken token)
    {
        JobMetadata.ValidateJobId(jobId);
        try
        {
            using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(usage, JobMetadata.JsonOptions));
            await container.GetBlobClient($"jobs/{jobId}/usage/{usage.RequestId}-{(usage.Completed ? "complete" : "started")}.json")
                .UploadAsync(stream, new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
                }, token);
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException)
        { throw new JobStorageException("Job usage could not be recorded.", exception); }
    }

    public async Task<IReadOnlyList<JobUsage>> ReadAsync(string jobId, CancellationToken token)
    {
        JobMetadata.ValidateJobId(jobId);
        var result = new List<JobUsage>();
        try
        {
            await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None,
                $"jobs/{jobId}/usage/", token))
            {
                var response = await container.GetBlobClient(blob.Name).DownloadStreamingAsync(cancellationToken: token);
                await using var stream = response.Value.Content;
                result.Add(await JsonSerializer.DeserializeAsync<JobUsage>(stream, JobMetadata.JsonOptions, token)
                    ?? throw new InvalidDataException("Empty job usage record."));
            }
            return result;
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException
            or JsonException or InvalidDataException)
        { throw new JobStorageException("Job usage could not be loaded.", exception); }
    }
}

public sealed class LocalJobUsageStorage(string root) : IJobUsageStorage
{
    private string DirectoryFor(string jobId)
    {
        JobMetadata.ValidateJobId(jobId);
        return Path.Combine(root, jobId, "usage");
    }

    public async Task AppendAsync(string jobId, JobUsage usage, CancellationToken token)
    {
        var directory = DirectoryFor(jobId);
        var path = Path.Combine(directory, $"{usage.RequestId}-{(usage.Completed ? "complete" : "started")}.json");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(usage, JobMetadata.JsonOptions), token);
            File.Move(temporary, path, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new JobStorageException("Job usage could not be recorded.", exception); }
        finally { File.Delete(temporary); }
    }

    public async Task<IReadOnlyList<JobUsage>> ReadAsync(string jobId, CancellationToken token)
    {
        var directory = DirectoryFor(jobId);
        try
        {
            if (!Directory.Exists(directory)) return [];
            var result = new List<JobUsage>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                await using var stream = File.OpenRead(path);
                result.Add(await JsonSerializer.DeserializeAsync<JobUsage>(stream, JobMetadata.JsonOptions, token)
                    ?? throw new InvalidDataException("Empty job usage record."));
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { throw new JobStorageException("Job usage could not be loaded.", exception); }
    }
}
