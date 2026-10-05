using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class MyVideosModel(
    IJobStorageService storage, ILanguageService languages, ILogger<MyVideosModel> logger) : PageModel
{
    public const int PageSize = 20;
    public IReadOnlyList<VideoJob> Jobs { get; private set; } = [];
    public int PageNumber { get; private set; }
    public int TotalCount { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public string? ErrorMessage { get; private set; }
    public int UnreadableCount { get; private set; }
    public string LanguageName(VideoJob job) => languages.Find(job.SelectedLanguage)?.DisplayName ?? job.SelectedLanguage;

    public async Task<IActionResult> OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            logger.LogWarning("Video history requested with invalid page number.");
            return BadRequest("Page number must be positive.");
        }
        var jobs = new List<VideoJob>();
        try
        {
            await foreach (var id in storage.ListJobIdsAsync(cancellationToken))
            {
                try
                {
                    var job = await storage.GetAsync(id, cancellationToken);
                    if (job is not null) { jobs.Add(job); }
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    logger.LogError(exception, "[Job: {JobId}] Video history metadata is unreadable.", id);
                    UnreadableCount++;
                }
            }
        }
        catch (Exception exception) when (exception is JobStorageException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Video history storage could not be read.");
            ErrorMessage = "Your videos could not be loaded. Storage is temporarily unavailable; please try again.";
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            PageNumber = 1;
            return Page();
        }
        TotalCount = jobs.Count;
        PageNumber = Math.Min(pageNumber, TotalPages);
        Jobs = jobs.OrderByDescending(job => job.CreatedUtc).ThenBy(job => job.JobId, StringComparer.Ordinal)
            .Skip((PageNumber - 1) * PageSize).Take(PageSize).ToArray();
        return Page();
    }

    public static string StatusLabel(JobStatus status) => status switch
    {
        JobStatus.Uploaded => "Queued",
        JobStatus.ExtractingAudio => "Extracting audio",
        JobStatus.AudioReady => "Audio extracted",
        JobStatus.Transcribing => "Transcribing",
        JobStatus.TranscriptReady => "Transcript ready",
        JobStatus.Translating => "Translating",
        JobStatus.AwaitingScriptReview => "Script review needed",
        JobStatus.GeneratingSpeech => "Generating translated audio",
        JobStatus.TranslatedAudioReady => "Audio ready for rendering approval",
        JobStatus.CreatingSubtitles => "Creating subtitles",
        JobStatus.RenderingVideo => "Rendering video",
        JobStatus.Completed => "Completed",
        JobStatus.Failed => "Failed",
        _ => status.ToString()
    };
}
