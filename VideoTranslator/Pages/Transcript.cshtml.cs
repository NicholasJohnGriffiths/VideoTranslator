using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class TranscriptModel(
    IJobStorageService jobs, ITranscriptStorageService transcripts,
    ILogger<TranscriptModel> logger) : PageModel
{
    public VideoJob Job { get; private set; } = new();
    public Transcript Transcript { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(jobId, "N", out _))
        {
            logger.LogWarning("Transcript requested with an invalid job identifier.");
            return NotFound();
        }
        try
        {
            var job = await jobs.GetAsync(jobId, cancellationToken);
            if (job is null)
            {
                logger.LogInformation("[Job: {JobId}] Transcript job not found.", jobId);
                return NotFound();
            }
            var transcript = await transcripts.GetTranscriptAsync(jobId, cancellationToken);
            if (transcript is null)
            {
                logger.LogInformation("[Job: {JobId}] Transcript not available yet.", jobId);
                return RedirectToPage("/Job", new { jobId });
            }
            Job = job;
            Transcript = transcript;
            return Page();
        }
        catch (JobStorageException exception)
        {
            logger.LogError(exception, "[Job: {JobId}] Transcript storage unavailable.", jobId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Transcript storage is temporarily unavailable. Please try again.");
        }
    }
}
