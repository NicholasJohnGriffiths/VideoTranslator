using System.Text.Json;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class BlobJobStorageService(
    BlobContainerClient container, IOptions<UploadOptions> options,
    IUploadValidator validator, ILogger<BlobJobStorageService> logger) : IJobStorageService, ITranscriptStorageService
{
    public async Task CreateAsync(VideoJob job, Stream video, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(job.JobId);
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"VideoTranslator-upload-{Guid.NewGuid():N}.mp4");
        var original = container.GetBlobClient($"jobs/{job.JobId}/original/original-video.mp4");
        var metadata = container.GetBlobClient($"jobs/{job.JobId}/job.json");
        var originalUploaded = false;
        var published = false;

        try
        {
            await UploadContent.CopyAndValidateAsync(
                video, temporaryPath, job.FileSizeBytes, options.Value.MaxFileSizeBytes,
                validator, cancellationToken);
            await using (var input = File.OpenRead(temporaryPath))
            {
                await original.UploadAsync(input, new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = "video/mp4" }
                }, cancellationToken);
                originalUploaded = true;
            }

            // Publish metadata last so job pages never announce an unfinished upload.
            await using var json = new MemoryStream(
                JsonSerializer.SerializeToUtf8Bytes(job, JobMetadata.JsonOptions));
            await metadata.UploadAsync(json, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" }
            }, cancellationToken);
            published = true;
            logger.LogInformation("[Job: {JobId}] Upload persisted to private Blob storage.", job.JobId);
        }
        catch (RequestFailedException exception)
        {
            logger.LogError(exception, "[Job: {JobId}] Azure upload failed.", job.JobId);
            throw new JobStorageException("Azure video storage is unavailable. Please try again.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            logger.LogError(exception, "[Job: {JobId}] Azure upload authentication failed.", job.JobId);
            throw new JobStorageException("Azure video storage authentication failed. Please contact the administrator.", exception);
        }
        finally
        {
            if (originalUploaded && !published)
            {
                await CleanUpBlobAsync(original, metadata, job.JobId);
            }

            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException exception)
            {
                logger.LogError(exception, "[Job: {JobId}] Could not remove temporary upload file.", job.JobId);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogError(exception, "[Job: {JobId}] Access denied removing temporary upload file.", job.JobId);
            }
        }
    }

    public async Task<VideoJob?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        var metadata = container.GetBlobClient($"jobs/{jobId}/job.json");
        try
        {
            var response = await metadata.DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var content = response.Value.Content;
            return await JobMetadata.ReadAsync(content, jobId, cancellationToken);
        }
        catch (RequestFailedException exception) when (
            exception.Status == 404 && exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString())
        {
            return null;
        }
        catch (RequestFailedException exception)
        {
            logger.LogError(exception, "[Job: {JobId}] Azure job retrieval failed.", jobId);
            throw new JobStorageException("Azure job details could not be retrieved. Please try again.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            logger.LogError(exception, "[Job: {JobId}] Azure job retrieval authentication failed.", jobId);
            throw new JobStorageException("Azure job storage authentication failed. Please contact the administrator.", exception);
        }
    }

    public async IAsyncEnumerable<string> ListJobIdsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pages = container.GetBlobsAsync(BlobTraits.None, BlobStates.None,
            prefix: "jobs/", cancellationToken: cancellationToken)
            .AsPages(pageSizeHint: 100);
        var enumerator = pages.GetAsyncEnumerator(cancellationToken);
        await using (enumerator)
        {
            while (true)
            {
                bool available;
                try
                {
                    available = await enumerator.MoveNextAsync();
                }
                catch (RequestFailedException exception)
                {
                    throw new JobStorageException("Azure pending jobs could not be listed.", exception);
                }
                catch (AuthenticationFailedException exception)
                {
                    throw new JobStorageException("Azure pending jobs authentication failed.", exception);
                }
                if (!available)
                {
                    yield break;
                }
                foreach (var blob in enumerator.Current.Values)
                {
                    var parts = blob.Name.Split('/');
                    if (parts.Length == 3 && parts[0] == "jobs" && parts[2] == "job.json"
                        && Guid.TryParseExact(parts[1], "N", out _))
                    {
                        yield return parts[1];
                    }
                }
            }
        }
    }

    public async Task DownloadVideoAsync(VideoJob job, string destinationPath, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(job.JobId);
        try
        {
            var response = await container.GetBlobClient($"jobs/{job.JobId}/original/original-video.mp4")
                .DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var input = response.Value.Content;
            await UploadContent.CopyAndValidateAsync(input, destinationPath, job.FileSizeBytes,
                options.Value.MaxFileSizeBytes, validator, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("The original video could not be downloaded from Azure.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Azure video download authentication failed.", exception);
        }
    }

    public async Task SaveAudioAsync(string jobId, string audioPath, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        try
        {
            await using var input = File.OpenRead(audioPath);
            await container.GetBlobClient($"jobs/{jobId}/working/original-audio.wav").UploadAsync(
                input, new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "audio/wav" }
                }, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("The extracted audio could not be saved in Azure.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Azure audio storage authentication failed.", exception);
        }
    }

    public async Task UpdateAsync(VideoJob job, JobStatus expectedStatus, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(job.JobId);
        var metadata = container.GetBlobClient($"jobs/{job.JobId}/job.json");
        try
        {
            var response = await metadata.DownloadStreamingAsync(cancellationToken: cancellationToken);
            VideoJob previous;
            await using (var input = response.Value.Content)
            {
                previous = await JobMetadata.ReadAsync(input, job.JobId, cancellationToken);
            }
            if (previous.Status != expectedStatus)
            {
                throw new JobStorageException("Job status changed; reload before continuing.",
                    new InvalidOperationException("Unexpected stored status."));
            }
            await using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(job, JobMetadata.JsonOptions));
            await metadata.UploadAsync(json, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = response.Value.Details.ETag },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" }
            }, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("Job status could not be updated in Azure.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Azure job update authentication failed.", exception);
        }
    }
    public async Task DownloadAudioAsync(string jobId, string destinationPath, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        try
        {
            var response = await container.GetBlobClient($"jobs/{jobId}/working/original-audio.wav")
                .DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var input = response.Value.Content;
            await TranscriptMetadata.CopyAudioAsync(input, destinationPath, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("Extracted audio could not be downloaded from Azure.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Azure audio download authentication failed.", exception);
        }
    }

    public async Task SaveTranscriptAsync(string jobId, Transcript transcript, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        TranscriptMetadata.Validate(transcript);
        try
        {
            await using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(transcript, JobMetadata.JsonOptions));
            await container.GetBlobClient($"jobs/{jobId}/transcript.json").UploadAsync(json,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" }
                }, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("English transcript could not be saved in Azure.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Azure transcript storage authentication failed.", exception);
        }
    }

    public async Task<Transcript?> GetTranscriptAsync(string jobId, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        try
        {
            var response = await container.GetBlobClient($"jobs/{jobId}/transcript.json")
                .DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var input = response.Value.Content;
            return await TranscriptMetadata.ReadAsync(input, cancellationToken);
        }
        catch (RequestFailedException exception) when (
            exception.Status == 404 && exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString())
        {
            return null;
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("English transcript could not be loaded from Azure.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Azure transcript retrieval authentication failed.", exception);
        }
    }

    private async Task CleanUpBlobAsync(BlobClient original, BlobClient metadata, string jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // A lost upload response can still mean Azure committed the metadata.
            if ((await metadata.ExistsAsync(timeout.Token)).Value)
            {
                logger.LogWarning("[Job: {JobId}] Metadata exists after an upload error; preserving original video.", jobId);
                return;
            }

            await original.DeleteIfExistsAsync(cancellationToken: timeout.Token);
        }
        catch (RequestFailedException exception)
        {
            logger.LogError(exception, "[Job: {JobId}] Could not clean up incomplete Blob upload.", jobId);
        }
        catch (AuthenticationFailedException exception)
        {
            logger.LogError(exception, "[Job: {JobId}] Authentication failed cleaning up incomplete Blob upload.", jobId);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            logger.LogError(exception, "[Job: {JobId}] Timed out cleaning up incomplete Blob upload.", jobId);
        }
    }
}
