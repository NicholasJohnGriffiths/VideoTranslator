using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ResultModel(IJobStorageService jobs, IRenderedVideoStorageService storage,
    ILogger<ResultModel> logger) : PageModel
{
    public VideoJob Job { get; private set; } = new();
    public RenderedVideoResult Result { get; private set; } = new();
    public Task<IActionResult> OnGetAsync(string jobId, CancellationToken token) => HandleAsync(jobId, null, false, token);
    public Task<IActionResult> OnGetVideoAsync(string jobId, CancellationToken token) => HandleAsync(jobId, RenderArtifact.Video, false, token);
    public Task<IActionResult> OnGetDownloadAsync(string jobId, CancellationToken token) => HandleAsync(jobId, RenderArtifact.Video, true, token);
    public Task<IActionResult> OnGetSubtitlesAsync(string jobId, CancellationToken token) => HandleAsync(jobId, RenderArtifact.Subtitles, false, token);
    public Task<IActionResult> OnGetDownloadSubtitlesAsync(string jobId, CancellationToken token) => HandleAsync(jobId, RenderArtifact.Subtitles, true, token);
    public Task<IActionResult> OnGetScriptAsync(string jobId, CancellationToken token) => HandleAsync(jobId, RenderArtifact.Script, true, token);

    private async Task<IActionResult> HandleAsync(string jobId, RenderArtifact? artifact, bool download, CancellationToken token)
    {
        if (!Guid.TryParseExact(jobId, "N", out _))
        { logger.LogWarning("Invalid result job identifier."); return NotFound(); }
        FileStream? stream = null;
        try
        {
            var job = await jobs.GetAsync(jobId, token);
            if (job is null) { logger.LogInformation("[Job: {JobId}] Result not found.", jobId); return NotFound(); }
            if (job.Status != JobStatus.Completed || job.ApprovedSpeech is null)
            { logger.LogInformation("[Job: {JobId}] Result not ready.", jobId); return RedirectToPage("/Job", new { jobId }); }
            SpeechGenerationMetadata.Validate(job.ApprovedSpeech, job.SelectedLanguage);
            var result = await storage.GetResultAsync(jobId, job.ApprovedSpeech.RequestId, token)
                ?? throw new InvalidDataException("Completed video metadata is missing.");
            RenderedVideoMetadata.Validate(result, job.ApprovedSpeech.RequestId);
            Job = job;
            Result = result;
            if (artifact is null) { return Page(); }
            var temporary = Path.Combine(Path.GetTempPath(), $"VideoTranslator-result-{Guid.NewGuid():N}.tmp");
            stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await storage.CopyAsync(jobId, result.RequestId, artifact.Value, stream, token);
            await RenderedVideoMetadata.VerifyAsync(stream, result.Files[artifact.Value], token);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            var type = artifact switch
            {
                RenderArtifact.Video => "video/mp4", RenderArtifact.Subtitles => "text/vtt; charset=utf-8",
                _ => "application/json; charset=utf-8"
            };
            var response = new FileStreamResult(stream, type) { EnableRangeProcessing = artifact == RenderArtifact.Video };
            if (download) { response.FileDownloadName = RenderedVideoMetadata.Name(artifact.Value); }
            stream = null;
            return response;
        }
        catch (Exception exception) when (exception is JobStorageException or IOException
            or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            logger.LogError(exception, "[Job: {JobId}] Final output unavailable or corrupt.", jobId);
            return StatusCode(503, "Final output could not be loaded. Contact the administrator.");
        }
        finally { if (stream is not null) { await stream.DisposeAsync(); } }
    }
}
