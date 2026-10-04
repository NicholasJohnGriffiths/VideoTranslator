using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class BlobTranslationStorageService(BlobContainerClient container) : ITranslationStorageService
{
    private BlobClient Blob(string jobId, string name)
    {
        JobMetadata.ValidateJobId(jobId);
        return container.GetBlobClient($"jobs/{jobId}/{name}");
    }

    public async Task<Translation?> GetTranslationAsync(string jobId, CancellationToken cancellationToken)
    {
        var data = await ReadAsync(Blob(jobId, "translation.json"), cancellationToken);
        if (data is null)
        {
            return null;
        }
        var translation = JsonSerializer.Deserialize<Translation>(data.Value.Bytes, JobMetadata.JsonOptions)
            ?? throw new InvalidDataException("Stored translation is empty.");
        TranslationMetadata.Validate(translation);
        return translation;
    }

    public async Task SaveTranslationAsync(string jobId, Translation translation, CancellationToken cancellationToken)
    {
        TranslationMetadata.Validate(translation);
        await WriteAsync(Blob(jobId, "translation.json"), translation,
            new BlobRequestConditions { IfNoneMatch = ETag.All }, cancellationToken);
    }

    public async Task<ScriptEditSnapshot> GetEditsAsync(string jobId, CancellationToken cancellationToken)
    {
        var data = await ReadAsync(Blob(jobId, "edited-script.json"), cancellationToken);
        return data is null ? new(new ScriptEdits(), null) : new(
            JsonSerializer.Deserialize<ScriptEdits>(data.Value.Bytes, JobMetadata.JsonOptions)
                ?? throw new InvalidDataException("Stored edits are empty."), data.Value.Revision);
    }

    public async Task SaveEditsAsync(string jobId, ScriptEdits edits, string? expectedRevision, CancellationToken cancellationToken)
    {
        var translation = await GetTranslationAsync(jobId, cancellationToken)
            ?? throw new InvalidDataException("Cannot edit a missing translation.");
        TranslationMetadata.ValidateEdits(translation, edits);
        try
        {
            await WriteAsync(Blob(jobId, "edited-script.json"), edits, expectedRevision is null
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = new ETag(expectedRevision) }, cancellationToken);
        }
        catch (JobStorageException exception) when (
            exception.InnerException is RequestFailedException { Status: 409 or 412 })
        {
            throw new ScriptConflictException();
        }
    }

    private static async Task<(byte[] Bytes, string Revision)?> ReadAsync(BlobClient blob, CancellationToken cancellationToken)
    {
        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken);
            return (response.Value.Content.ToArray(), response.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException exception) when (
            exception.Status == 404 && exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString())
        {
            return null;
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("Script storage is unavailable. Please try again.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Script storage authentication failed. Contact the administrator.", exception);
        }
    }

    private static async Task WriteAsync<T>(BlobClient blob, T value,
        BlobRequestConditions conditions, CancellationToken cancellationToken)
    {
        try
        {
            await using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value, JobMetadata.JsonOptions));
            await blob.UploadAsync(json, new BlobUploadOptions
            {
                Conditions = conditions,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" }
            }, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("Script could not be saved. Please try again.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Script storage authentication failed. Contact the administrator.", exception);
        }
    }
}
