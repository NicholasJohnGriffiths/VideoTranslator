using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class VoicePreviewModel(IJobStorageService jobs, ITranslationStorageService translations,
    IVoicePreviewStorageService previews, ILogger<VoicePreviewModel> logger) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string jobId, int sequence, string key, CancellationToken cancellationToken)
    {
        try
        {
            VoicePreviewKey.Validate(jobId, sequence, key);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("Invalid voice preview URL rejected.");
            return NotFound();
        }
        try
        {
            var job = await jobs.GetAsync(jobId, cancellationToken);
            if (job is null)
            {
                logger.LogInformation("[Job: {JobId}] Voice preview job not found.", jobId);
                return NotFound();
            }
            var translation = await translations.GetTranslationAsync(jobId, cancellationToken);
            if (translation is null || !translation.Segments.Any(segment => segment.Sequence == sequence))
            {
                logger.LogInformation("[Job: {JobId}] Voice preview segment not found.", jobId);
                return NotFound();
            }
            var audio = await previews.GetAsync(jobId, sequence, key, cancellationToken);
            if (audio is null)
            {
                logger.LogInformation("[Job: {JobId}] Voice preview audio not found.", jobId);
                return NotFound();
            }
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return new FileContentResult(audio, "audio/wav") { EnableRangeProcessing = true };
        }
        catch (Exception exception) when (exception is JobStorageException or IOException
            or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            logger.LogError(exception, "[Job: {JobId}] Voice preview audio could not be loaded.", jobId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Voice preview audio is unavailable. Try again later.");
        }
    }
}
