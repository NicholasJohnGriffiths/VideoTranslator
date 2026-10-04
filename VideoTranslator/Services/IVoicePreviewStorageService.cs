namespace VideoTranslator.Services;

public interface IVoicePreviewStorageService
{
    Task<byte[]?> GetAsync(string jobId, int sequence, string key, CancellationToken cancellationToken);
    Task SaveAsync(string jobId, int sequence, string key, byte[] audio, CancellationToken cancellationToken);
}
