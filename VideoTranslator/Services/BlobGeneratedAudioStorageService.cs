using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class BlobGeneratedAudioStorageService(BlobContainerClient container) : IGeneratedAudioStorageService
{
    private BlobClient Blob(string jobId, string requestId, string name)
    {
        GeneratedAudioStorage.ValidateIds(jobId, requestId);
        return container.GetBlobClient($"jobs/{jobId}/generated/{requestId}/{name}");
    }

    public async Task<TimedAudioResult?> GetResultAsync(string jobId, string requestId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Blob(jobId, requestId, "timing.json").DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var stream = response.Value.Content;
            return await GeneratedAudioStorage.ReadAsync(stream, requestId, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404
            && exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString()) { return null; }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("Generated audio metadata is unavailable.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Generated audio authentication failed.", exception);
        }
    }

    public async Task SaveAsync(string jobId, TimedAudioResult result, string audioPath, CancellationToken cancellationToken)
    {
        try
        {
            if (await GetResultAsync(jobId, result.RequestId, cancellationToken) is not null)
            {
                throw new JobStorageException(
                    "Audio was committed by another worker. Saved audio was not overwritten; reload the job before retrying.",
                    new InvalidOperationException("Committed audio cannot be overwritten."));
            }
            await using var source = File.OpenRead(audioPath);
            if (source.Length <= 44 || source.Length > 600_000_000)
            {
                throw new InvalidDataException("Generated audio is empty or oversized.");
            }
            await Blob(jobId, result.RequestId, "translated-audio.wav").UploadAsync(source,
                new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "audio/wav" } }, cancellationToken);
            await using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(result, JobMetadata.JsonOptions));
            await Blob(jobId, result.RequestId, "timing.json").UploadAsync(json, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" }
            }, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("Generated audio could not be saved.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Generated audio authentication failed.", exception);
        }
    }

    public async Task CopyAudioAsync(string jobId, string requestId, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Blob(jobId, requestId, "translated-audio.wav").DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var source = response.Value.Content;
            await GeneratedAudioStorage.CopyAsync(source, destination, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("Generated audio could not be downloaded.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Generated audio authentication failed.", exception);
        }
    }
}
