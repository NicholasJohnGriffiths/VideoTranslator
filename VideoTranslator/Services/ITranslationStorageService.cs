using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ITranslationStorageService
{
    Task<Translation?> GetTranslationAsync(string jobId, CancellationToken cancellationToken);
    Task SaveTranslationAsync(string jobId, Translation translation, CancellationToken cancellationToken);
    Task<ScriptEditSnapshot> GetEditsAsync(string jobId, CancellationToken cancellationToken);
    Task SaveEditsAsync(string jobId, ScriptEdits edits, string? expectedRevision, CancellationToken cancellationToken);
}
