using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class BlobRenderedVideoStorageService(BlobContainerClient container) : IRenderedVideoStorageService
{
    private BlobClient Blob(string jobId, string requestId, string name)
    {
        GeneratedAudioStorage.ValidateIds(jobId, requestId);
        return container.GetBlobClient($"jobs/{jobId}/rendered/{requestId}/{name}");
    }
    public async Task<RenderedVideoResult?> GetResultAsync(string jobId, string requestId, CancellationToken token)
    {
        try
        {
            var response = await Blob(jobId, requestId, "result.json").DownloadStreamingAsync(cancellationToken: token);
            await using var stream = response.Value.Content;
            return await RenderedVideoMetadata.ReadAsync(stream, requestId, token);
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound") { return null; }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException)
        { throw new JobStorageException("Rendered metadata is unavailable.", exception); }
    }
    public async Task SaveArtifactAsync(string jobId, string requestId, RenderArtifact artifact, string path, CancellationToken token)
    {
        if (await GetResultAsync(jobId, requestId, token) is not null) { throw new InvalidDataException("Committed rendering cannot be overwritten."); }
        await RenderedVideoMetadata.InspectAsync(path, artifact, token);
        try
        {
            await using var stream = File.OpenRead(path);
            await Blob(jobId, requestId, RenderedVideoMetadata.Name(artifact)).UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = artifact switch
                {
                    RenderArtifact.Video => "video/mp4", RenderArtifact.Subtitles => "text/vtt; charset=utf-8",
                    _ => "application/json; charset=utf-8"
                }}
            }, token);
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException)
        { throw new JobStorageException("Rendered artifact could not be saved.", exception); }
    }
    public async Task CopyAsync(string jobId, string requestId, RenderArtifact artifact, Stream destination, CancellationToken token)
    {
        try
        {
            var response = await Blob(jobId, requestId, RenderedVideoMetadata.Name(artifact)).DownloadStreamingAsync(cancellationToken: token);
            await using var source = response.Value.Content;
            await RenderedVideoMetadata.CopyAsync(source, destination, artifact, token);
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException)
        { throw new JobStorageException("Rendered artifact could not be loaded.", exception); }
    }
    public async Task CommitAsync(string jobId, RenderedVideoResult result, CancellationToken token)
    {
        RenderedVideoMetadata.Validate(result, result.RequestId);
        try
        {
            foreach (var artifact in Enum.GetValues<RenderArtifact>())
            {
                var properties = await Blob(jobId, result.RequestId, RenderedVideoMetadata.Name(artifact)).GetPropertiesAsync(cancellationToken: token);
                if (properties.Value.ContentLength != result.Files[artifact].Length)
                { throw new InvalidDataException("Cannot commit missing or inconsistent rendered artifacts."); }
            }
            await using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(result, JobMetadata.JsonOptions));
            await Blob(jobId, result.RequestId, "result.json").UploadAsync(json, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" }
            }, token);
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException)
        { throw new JobStorageException("Rendered output could not be committed.", exception); }
    }
}
