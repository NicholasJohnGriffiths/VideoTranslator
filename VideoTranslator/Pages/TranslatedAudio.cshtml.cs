using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class TranslatedAudioModel(IJobStorageService jobs, IGeneratedAudioStorageService audio,
    ILogger<TranslatedAudioModel> logger) : PageModel
{
    public VideoJob Job { get; private set; } = new();
    public TimedAudioResult Result { get; private set; } = new();
    public Task<IActionResult> OnGetAsync(string jobId, CancellationToken cancellationToken) =>
        HandleAsync(jobId, serveFile: false, download: false, cancellationToken);
    public Task<IActionResult> OnGetFileAsync(string jobId, CancellationToken cancellationToken) =>
        HandleAsync(jobId, serveFile: true, download: false, cancellationToken);
    public Task<IActionResult> OnGetDownloadAsync(string jobId, CancellationToken cancellationToken) =>
        HandleAsync(jobId, serveFile: true, download: true, cancellationToken);

    public async Task<IActionResult> OnPostRenderAsync(string jobId, bool confirmRendering, CancellationToken token)
    {
        if (!Guid.TryParseExact(jobId, "N", out _))
        {
            logger.LogWarning("Invalid rendering job identifier rejected.");
            return NotFound();
        }
        if (!confirmRendering)
        {
            logger.LogWarning("[Job: {JobId}] Rendering confirmation missing.", jobId);
            return BadRequest("Confirm replacing the original audio before rendering.");
        }
        try
        {
            var job = await jobs.GetAsync(jobId, token);
            if (job is null) { logger.LogInformation("[Job: {JobId}] Render job not found.", jobId); return NotFound(); }
            if (job.Status != JobStatus.TranslatedAudioReady || job.ApprovedSpeech is null)
            {
                logger.LogWarning("[Job: {JobId}] Duplicate or premature rendering rejected.", jobId);
                return StatusCode(409, "Rendering can only be approved for a ready translated audio track.");
            }
            var result = await audio.GetResultAsync(jobId, job.ApprovedSpeech.RequestId, token)
                ?? throw new InvalidDataException("Audio metadata missing.");
            SpeechGenerationMetadata.ValidateResult(result, job.ApprovedSpeech);
            job.TransitionTo(JobStatus.CreatingSubtitles);
            await jobs.UpdateAsync(job, JobStatus.TranslatedAudioReady, token);
            return RedirectToPage("/Job", new { jobId });
        }
        catch (Exception exception) when (exception is JobStorageException or IOException
            or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            logger.LogError(exception, "[Job: {JobId}] Rendering approval failed.", jobId);
            return StatusCode(503, "Rendering could not be queued. Reload the job before trying again.");
        }
    }

    private async Task<IActionResult> HandleAsync(string jobId, bool serveFile, bool download, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(jobId, "N", out _))
        {
            logger.LogWarning("Invalid translated audio job identifier rejected.");
            return NotFound();
        }
        FileStream? stream = null;
        try
        {
            var job = await jobs.GetAsync(jobId, cancellationToken);
            if (job is null)
            {
                logger.LogInformation("[Job: {JobId}] Audio job not found.", jobId);
                return NotFound();
            }
            if (job.Status is not (JobStatus.TranslatedAudioReady or JobStatus.CreatingSubtitles or JobStatus.RenderingVideo or JobStatus.Completed)
                || job.ApprovedSpeech is null)
            {
                logger.LogInformation("[Job: {JobId}] Full translated audio not ready.", jobId);
                return RedirectToPage("/Job", new { jobId });
            }
            var result = await audio.GetResultAsync(jobId, job.ApprovedSpeech.RequestId, cancellationToken)
                ?? throw new InvalidDataException("Completed audio metadata is missing.");
            SpeechGenerationMetadata.ValidateResult(result, job.ApprovedSpeech);
            Job = job;
            Result = result;
            if (!serveFile)
            {
                return Page();
            }
            var temporary = Path.Combine(Path.GetTempPath(), $"VideoTranslator-download-{Guid.NewGuid():N}.wav");
            stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await audio.CopyAudioAsync(jobId, job.ApprovedSpeech.RequestId, stream, cancellationToken);
            stream.Position = 0;
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            var response = new FileStreamResult(stream, "audio/wav") { EnableRangeProcessing = true };
            if (download)
            {
                response.FileDownloadName = $"translated-{job.SelectedLanguage}.wav";
            }
            stream = null;
            return response;
        }
        catch (Exception exception) when (exception is JobStorageException or IOException
            or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            logger.LogError(exception, "[Job: {JobId}] Full audio storage unavailable or invalid.", jobId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Translated audio could not be loaded. Contact the administrator.");
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
    }
}
