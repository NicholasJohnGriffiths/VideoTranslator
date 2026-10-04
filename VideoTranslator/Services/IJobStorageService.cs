using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface IJobStorageService
{
    Task CreateAsync(VideoJob job, Stream video, CancellationToken cancellationToken);
    Task<VideoJob?> GetAsync(string jobId, CancellationToken cancellationToken);
    IAsyncEnumerable<string> ListJobIdsAsync(CancellationToken cancellationToken);
    Task DownloadVideoAsync(VideoJob job, string destinationPath, CancellationToken cancellationToken);
    Task SaveAudioAsync(string jobId, string audioPath, CancellationToken cancellationToken);
    Task UpdateAsync(VideoJob job, JobStatus expectedStatus, CancellationToken cancellationToken);
}
