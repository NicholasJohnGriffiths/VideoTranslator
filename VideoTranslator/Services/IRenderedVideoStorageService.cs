using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface IRenderedVideoStorageService
{
    Task<RenderedVideoResult?> GetResultAsync(string jobId, string requestId, CancellationToken cancellationToken);
    Task SaveArtifactAsync(string jobId, string requestId, RenderArtifact artifact, string path, CancellationToken cancellationToken);
    Task CopyAsync(string jobId, string requestId, RenderArtifact artifact, Stream destination, CancellationToken cancellationToken);
    Task CommitAsync(string jobId, RenderedVideoResult result, CancellationToken cancellationToken);
}
