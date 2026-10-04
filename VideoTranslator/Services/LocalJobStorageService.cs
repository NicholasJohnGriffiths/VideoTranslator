using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class LocalJobStorageService : IJobStorageService, ITranscriptStorageService
{
    private readonly string root;
    private readonly long maxFileSize;
    private readonly IUploadValidator validator;
    private readonly ILogger<LocalJobStorageService> logger;
    private readonly SemaphoreSlim metadataLock = new(1, 1);

    public LocalJobStorageService(
        IHostEnvironment environment, IOptions<LocalStorageOptions> storageOptions,
        IOptions<UploadOptions> uploadOptions, IUploadValidator validator,
        ILogger<LocalJobStorageService> logger)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Local job storage is only supported in Development.");
        }

        root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, storageOptions.Value.RootPath));
        var webRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "wwwroot"));
        if (root.Equals(webRoot, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Job storage must be outside the public web root.");
        }

        maxFileSize = uploadOptions.Value.MaxFileSizeBytes;
        this.validator = validator;
        this.logger = logger;
    }

    public async Task CreateAsync(VideoJob job, Stream video, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(job.JobId);
        Directory.CreateDirectory(root);
        var finalDirectory = Path.Combine(root, job.JobId);
        var stagingDirectory = Path.Combine(root, $".upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var videoPath = Path.Combine(stagingDirectory, "original-video.mp4");
            await UploadContent.CopyAndValidateAsync(
                video, videoPath, job.FileSizeBytes, maxFileSize, validator, cancellationToken);
            await using (var metadata = new FileStream(Path.Combine(stagingDirectory, "job.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(metadata, job, JobMetadata.JsonOptions, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, finalDirectory);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (IOException exception)
                {
                    logger.LogError(exception, "[Job: {JobId}] Could not clean up staged upload.", job.JobId);
                }
                catch (UnauthorizedAccessException exception)
                {
                    logger.LogError(exception, "[Job: {JobId}] Access denied cleaning up staged upload.", job.JobId);
                }
            }
        }
    }

    public async Task<VideoJob?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        var directory = Path.Combine(root, jobId);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        await using var metadata = new FileStream(Path.Combine(directory, "job.json"),
            FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await JobMetadata.ReadAsync(metadata, jobId, cancellationToken);
    }

    public async IAsyncEnumerable<string> ListJobIdsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (Guid.TryParseExact(id, "N", out _) && File.Exists(Path.Combine(directory, "job.json")))
            {
                yield return id;
            }
        }
        await Task.CompletedTask;
    }

    public async Task DownloadVideoAsync(VideoJob job, string destinationPath, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(job.JobId);
        await using var input = File.OpenRead(Path.Combine(root, job.JobId, "original-video.mp4"));
        await UploadContent.CopyAndValidateAsync(
            input, destinationPath, job.FileSizeBytes, maxFileSize, validator, cancellationToken);
    }

    public async Task SaveAudioAsync(string jobId, string audioPath, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        var directory = Path.Combine(root, jobId);
        var temporaryPath = Path.Combine(directory, $"audio-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = File.OpenRead(audioPath))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            File.Move(temporaryPath, Path.Combine(directory, "original-audio.wav"), overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public async Task UpdateAsync(VideoJob job, JobStatus expectedStatus, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(job.JobId);
        var temporaryPath = Path.Combine(root, job.JobId, $"metadata-{Guid.NewGuid():N}.tmp");
        await metadataLock.WaitAsync(cancellationToken);
        try
        {
            var previous = await GetAsync(job.JobId, cancellationToken)
                ?? throw new InvalidDataException("Cannot update a missing job.");
            if (previous.Status != expectedStatus)
            {
                throw new JobStorageException("Job status changed; reload before continuing.",
                    new InvalidOperationException("Unexpected stored status."));
            }
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, job, JobMetadata.JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, Path.Combine(root, job.JobId, "job.json"), overwrite: true);
        }
        finally
        {
            metadataLock.Release();
            File.Delete(temporaryPath);
        }
    }

    public async Task DownloadAudioAsync(string jobId, string destinationPath, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        await using var input = File.OpenRead(Path.Combine(root, jobId, "original-audio.wav"));
        await TranscriptMetadata.CopyAudioAsync(input, destinationPath, cancellationToken);
    }

    public async Task SaveTranscriptAsync(string jobId, Transcript transcript, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        TranscriptMetadata.Validate(transcript);
        var directory = Path.Combine(root, jobId);
        var temporaryPath = Path.Combine(directory, $"transcript-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, transcript, JobMetadata.JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, Path.Combine(directory, "transcript.json"), overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public async Task<Transcript?> GetTranscriptAsync(string jobId, CancellationToken cancellationToken)
    {
        JobMetadata.ValidateJobId(jobId);
        var path = Path.Combine(root, jobId, "transcript.json");
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await TranscriptMetadata.ReadAsync(input, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
