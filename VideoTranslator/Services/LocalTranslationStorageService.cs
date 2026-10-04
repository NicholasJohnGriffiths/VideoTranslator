using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class LocalTranslationStorageService : ITranslationStorageService
{
    private readonly string root;
    private readonly SemaphoreSlim editLock = new(1, 1);

    public LocalTranslationStorageService(IHostEnvironment environment, IOptions<LocalStorageOptions> options)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Local script storage is Development-only.");
        }
        root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.RootPath));
    }

    private string PathFor(string jobId, string name)
    {
        JobMetadata.ValidateJobId(jobId);
        return Path.Combine(root, jobId, name);
    }

    public async Task<Translation?> GetTranslationAsync(string jobId, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(PathFor(jobId, "translation.json"), cancellationToken);
        if (bytes is null)
        {
            return null;
        }
        var translation = JsonSerializer.Deserialize<Translation>(bytes, JobMetadata.JsonOptions)
            ?? throw new InvalidDataException("Stored translation is empty.");
        TranslationMetadata.Validate(translation);
        return translation;
    }

    public async Task SaveTranslationAsync(string jobId, Translation translation, CancellationToken cancellationToken)
    {
        TranslationMetadata.Validate(translation);
        await WriteAsync(PathFor(jobId, "translation.json"), translation, overwrite: false, cancellationToken);
    }

    public async Task<ScriptEditSnapshot> GetEditsAsync(string jobId, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(PathFor(jobId, "edited-script.json"), cancellationToken);
        if (bytes is null)
        {
            return new(new ScriptEdits(), null);
        }
        var snapshot = JsonSerializer.Deserialize<ScriptEditSnapshot>(bytes, JobMetadata.JsonOptions)
            ?? throw new InvalidDataException("Stored edits are empty.");
        if (snapshot.Edits is null || !Guid.TryParseExact(snapshot.Revision, "N", out _))
        {
            throw new InvalidDataException("Stored edits have an invalid revision.");
        }
        return snapshot;
    }

    public async Task SaveEditsAsync(string jobId, ScriptEdits edits, string? expectedRevision, CancellationToken cancellationToken)
    {
        var translation = await GetTranslationAsync(jobId, cancellationToken)
            ?? throw new InvalidDataException("Cannot edit a missing translation.");
        TranslationMetadata.ValidateEdits(translation, edits);
        await editLock.WaitAsync(cancellationToken);
        try
        {
            var previous = await GetEditsAsync(jobId, cancellationToken);
            if (previous.Revision != expectedRevision)
            {
                throw new ScriptConflictException();
            }
            await WriteAsync(PathFor(jobId, "edited-script.json"),
                new ScriptEditSnapshot(edits, Guid.NewGuid().ToString("N")), overwrite: true, cancellationToken);
        }
        finally
        {
            editLock.Release();
        }
    }

    private static async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var output = new MemoryStream();
            await input.CopyToAsync(output, cancellationToken);
            return output.ToArray();
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

    private static async Task WriteAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, value, JobMetadata.JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
