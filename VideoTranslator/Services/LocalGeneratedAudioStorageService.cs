using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class LocalGeneratedAudioStorageService : IGeneratedAudioStorageService
{
    private readonly string root;
    public LocalGeneratedAudioStorageService(IHostEnvironment environment, IOptions<LocalStorageOptions> options)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Local generated audio is Development-only.");
        }
        root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.RootPath));
    }

    private string Folder(string jobId, string requestId)
    {
        GeneratedAudioStorage.ValidateIds(jobId, requestId);
        return Path.Combine(root, jobId, "generated", requestId);
    }

    public async Task<TimedAudioResult?> GetResultAsync(string jobId, string requestId, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(Path.Combine(Folder(jobId, requestId), "timing.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await GeneratedAudioStorage.ReadAsync(input, requestId, cancellationToken);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task SaveAsync(string jobId, TimedAudioResult result, string audioPath, CancellationToken cancellationToken)
    {
        var folder = Folder(jobId, result.RequestId);
        Directory.CreateDirectory(folder);
        var temporaryAudio = Path.Combine(folder, $"audio-{Guid.NewGuid():N}.tmp");
        var temporaryJson = Path.Combine(folder, $"timing-{Guid.NewGuid():N}.tmp");
        try
        {
            if (await GetResultAsync(jobId, result.RequestId, cancellationToken) is not null)
            {
                throw new JobStorageException(
                    "Audio was committed by another worker. Saved audio was not overwritten; reload the job before retrying.",
                    new InvalidOperationException("Committed audio cannot be overwritten."));
            }
            await using (var source = File.OpenRead(audioPath))
            await using (var destination = File.Create(temporaryAudio))
            {
                await GeneratedAudioStorage.CopyAsync(source, destination, cancellationToken);
            }
            File.Move(temporaryAudio, Path.Combine(folder, "translated-audio.wav"), overwrite: true);
            await using (var json = File.Create(temporaryJson))
            {
                await JsonSerializer.SerializeAsync(json, result, JobMetadata.JsonOptions, cancellationToken);
            }
            File.Move(temporaryJson, Path.Combine(folder, "timing.json"), overwrite: false);
        }
        finally
        {
            File.Delete(temporaryAudio);
            File.Delete(temporaryJson);
        }
    }

    public async Task CopyAudioAsync(string jobId, string requestId, Stream destination, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(Path.Combine(Folder(jobId, requestId), "translated-audio.wav"));
        await GeneratedAudioStorage.CopyAsync(source, destination, cancellationToken);
    }
}
