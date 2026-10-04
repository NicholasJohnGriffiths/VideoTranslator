using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class LocalRenderedVideoStorageService : IRenderedVideoStorageService
{
    private readonly string root;
    public LocalRenderedVideoStorageService(IHostEnvironment environment, IOptions<LocalStorageOptions> options)
    {
        if (!environment.IsDevelopment()) { throw new InvalidOperationException("Local rendered output is Development-only."); }
        root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.RootPath));
    }
    private string Folder(string jobId, string requestId)
    {
        GeneratedAudioStorage.ValidateIds(jobId, requestId);
        return Path.Combine(root, jobId, "rendered", requestId);
    }
    public async Task<RenderedVideoResult?> GetResultAsync(string jobId, string requestId, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(Path.Combine(Folder(jobId, requestId), "result.json"));
            return await RenderedVideoMetadata.ReadAsync(stream, requestId, token);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public async Task SaveArtifactAsync(string jobId, string requestId, RenderArtifact artifact, string path, CancellationToken token)
    {
        var folder = Folder(jobId, requestId);
        Directory.CreateDirectory(folder);
        if (await GetResultAsync(jobId, requestId, token) is not null) { throw new InvalidDataException("Committed rendering cannot be overwritten."); }
        var temporary = Path.Combine(folder, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = File.OpenRead(path))
            await using (var destination = File.Create(temporary))
            {
                await RenderedVideoMetadata.CopyAsync(source, destination, artifact, token);
            }
            File.Move(temporary, Path.Combine(folder, RenderedVideoMetadata.Name(artifact)), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
    public async Task CopyAsync(string jobId, string requestId, RenderArtifact artifact, Stream destination, CancellationToken token)
    {
        await using var source = File.OpenRead(Path.Combine(Folder(jobId, requestId), RenderedVideoMetadata.Name(artifact)));
        await RenderedVideoMetadata.CopyAsync(source, destination, artifact, token);
    }
    public async Task CommitAsync(string jobId, RenderedVideoResult result, CancellationToken token)
    {
        RenderedVideoMetadata.Validate(result, result.RequestId);
        var folder = Folder(jobId, result.RequestId);
        foreach (var artifact in Enum.GetValues<RenderArtifact>())
        {
            var actual = await RenderedVideoMetadata.InspectAsync(Path.Combine(folder, RenderedVideoMetadata.Name(artifact)), artifact, token);
            if (actual != result.Files[artifact]) { throw new InvalidDataException("Cannot commit inconsistent rendered files."); }
        }
        var temporary = Path.Combine(folder, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(result, JobMetadata.JsonOptions), token);
            File.Move(temporary, Path.Combine(folder, "result.json"), overwrite: false);
        }
        finally { File.Delete(temporary); }
    }
}
