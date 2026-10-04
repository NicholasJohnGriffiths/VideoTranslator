using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class UploadModel(
    IVideoService videos, ILanguageService languages, IOptions<UploadOptions> options,
    ILogger<UploadModel> logger) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Choose an English MP4 video.")]
    public IFormFile? Video { get; set; }

    [BindProperty, Required(ErrorMessage = "Select a target language.")]
    public string SelectedLanguage { get; set; } = string.Empty;

    public IReadOnlyList<LanguageOption> Languages => languages.GetLanguages();
    public long MaxFileSizeMb => options.Value.MaxFileSizeBytes / 1024 / 1024;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || Video is null)
        {
            return Page();
        }

        try
        {
            await using var content = Video.OpenReadStream();
            var job = await videos.UploadAsync(content, Video.FileName, Video.ContentType,
                Video.Length, SelectedLanguage, cancellationToken);
            return RedirectToPage("/Job", new { jobId = job.JobId });
        }
        catch (UploadValidationException exception)
        {
            logger.LogWarning("Video upload rejected: {Reason}", exception.Message);
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Video upload could not be saved.");
            ModelState.AddModelError(string.Empty, "The video could not be saved. Please try again.");
            return Page();
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogError(exception, "Video upload storage access denied.");
            ModelState.AddModelError(string.Empty, "Video storage is unavailable. Please contact the administrator.");
            return Page();
        }
        catch (JobStorageException exception)
        {
            logger.LogError(exception, "Azure video upload failed.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }
}
