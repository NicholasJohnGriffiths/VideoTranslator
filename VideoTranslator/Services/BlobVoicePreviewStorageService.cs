using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace VideoTranslator.Services;

public sealed class BlobVoicePreviewStorageService(BlobContainerClient container) : IVoicePreviewStorageService
{
    private BlobClient Blob(string jobId, int sequence, string key)
    {
        VoicePreviewKey.Validate(jobId, sequence, key);
        return container.GetBlobClient($"jobs/{jobId}/previews/{sequence}-{key}.wav");
    }

    public async Task<byte[]?> GetAsync(string jobId, int sequence, string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Blob(jobId, sequence, key).DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var stream = response.Value.Content;
            var bytes = await SpeechWaveAudio.ReadBoundedAsync(stream, cancellationToken);
            SpeechWaveAudio.Validate(bytes);
            return bytes;
        }
        catch (RequestFailedException exception) when (exception.Status == 404
            && exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString()) { return null; }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("Voice preview storage is unavailable. Try again later.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Voice preview storage authentication failed. Contact the administrator.", exception);
        }
    }

    public async Task SaveAsync(string jobId, int sequence, string key, byte[] audio, CancellationToken cancellationToken)
    {
        SpeechWaveAudio.Validate(audio);
        try
        {
            await using var stream = new MemoryStream(audio, writable: false);
            await Blob(jobId, sequence, key).UploadAsync(stream, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "audio/wav" }
            }, cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new JobStorageException("Voice preview could not be saved. Try again later.", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new JobStorageException("Voice preview storage authentication failed. Contact the administrator.", exception);
        }
    }
}
