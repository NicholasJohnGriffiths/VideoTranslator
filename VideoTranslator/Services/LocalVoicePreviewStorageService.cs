using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class LocalVoicePreviewStorageService : IVoicePreviewStorageService
{
    private readonly string root;
    public LocalVoicePreviewStorageService(IHostEnvironment environment, IOptions<LocalStorageOptions> options)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Local preview storage is Development-only.");
        }
        root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.RootPath));
    }

    private string PathFor(string jobId, int sequence, string key)
    {
        VoicePreviewKey.Validate(jobId, sequence, key);
        return Path.Combine(root, jobId, "previews", $"{sequence}-{key}.wav");
    }

    public async Task<byte[]?> GetAsync(string jobId, int sequence, string key, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(PathFor(jobId, sequence, key), FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            var bytes = await SpeechWaveAudio.ReadBoundedAsync(input, cancellationToken);
            SpeechWaveAudio.Validate(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task SaveAsync(string jobId, int sequence, string key, byte[] audio, CancellationToken cancellationToken)
    {
        SpeechWaveAudio.Validate(audio);
        var path = PathFor(jobId, sequence, key);
        if (!File.Exists(Path.Combine(root, jobId, "job.json")))
        {
            throw new InvalidDataException("Cannot save a preview for a missing job.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, audio, cancellationToken);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
