using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class JobModel(
    IJobStorageService storage, ILanguageService languages, ILogger<JobModel> logger) : PageModel
{
    public VideoJob Job { get; private set; } = new();
    public LanguageOption? Language { get; private set; }

    public async Task<IActionResult> OnGetAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(jobId, "N", out _))
        {
            logger.LogWarning("Job page requested with an invalid identifier.");
            return NotFound();
        }

        VideoJob? job;
        try
        {
            job = await storage.GetAsync(jobId, cancellationToken);
        }
        catch (JobStorageException exception)
        {
            logger.LogError(exception, "[Job: {JobId}] Job details could not be loaded.", jobId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Job storage is temporarily unavailable. Please try again.");
        }
        if (job is null)
        {
            logger.LogInformation("[Job: {JobId}] Job not found.", jobId);
            return NotFound();
        }

        Job = job;
        Language = languages.Find(Job.SelectedLanguage);
        return Page();
    }
}
