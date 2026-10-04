using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ScriptModel(IJobStorageService jobs, ScriptEditingService editor,
    ILanguageService languages, ILogger<ScriptModel> logger, VoicePreviewService? previews = null,
    SpeechGenerationApprovalService? approval = null) : PageModel
{
    public VideoJob Job { get; private set; } = new();
    public Translation Translation { get; private set; } = new();
    public LanguageOption Language { get; private set; } = new();
    [BindProperty]
    public List<SegmentInput> Segments { get; set; } = [];
    [BindProperty]
    [StringLength(128)]
    public string? Revision { get; set; }
    [BindProperty]
    public bool ApprovalConfirmed { get; set; }
    public bool Saved { get; private set; }
    public bool CanEdit => Job.Status == JobStatus.AwaitingScriptReview;
    public bool CanPreview => CanEdit && previews is not null;
    public VoicePreview? Preview { get; private set; }
    public double PreviewSlotSeconds { get; private set; }

    public sealed class SegmentInput
    {
        public int Sequence { get; set; }
        [StringLength(8000)]
        public string? Text { get; set; }
        public bool UseOriginal { get; set; }
    }

    public Task<IActionResult> OnGetAsync(string jobId, bool saved, CancellationToken cancellationToken) =>
        HandleAsync(jobId, isPost: false, saved, cancellationToken);

    public Task<IActionResult> OnPostAsync(string jobId, CancellationToken cancellationToken) =>
        HandleAsync(jobId, isPost: true, saved: false, cancellationToken);

    public Task<IActionResult> OnPostPreviewAsync(string jobId, int sequence, CancellationToken cancellationToken) =>
        HandleAsync(jobId, isPost: true, saved: false, cancellationToken, sequence);

    public Task<IActionResult> OnPostGenerateAsync(string jobId, CancellationToken cancellationToken) =>
        HandleAsync(jobId, isPost: true, saved: false, cancellationToken, generate: true);

    private async Task<IActionResult> HandleAsync(string jobId, bool isPost, bool saved,
        CancellationToken cancellationToken, int? previewSequence = null, bool generate = false)
    {
        if (!Guid.TryParseExact(jobId, "N", out _))
        {
            logger.LogWarning("Script requested with an invalid job identifier.");
            return NotFound();
        }
        try
        {
            var job = await jobs.GetAsync(jobId, cancellationToken);
            if (job is null)
            {
                logger.LogInformation("[Job: {JobId}] Script job not found.", jobId);
                return NotFound();
            }
            var review = await editor.GetAsync(job, cancellationToken);
            if (review is null)
            {
                logger.LogInformation("[Job: {JobId}] Translation not available yet.", jobId);
                return RedirectToPage("/Job", new { jobId });
            }
            Job = job;
            Translation = review.Translation;
            Language = languages.Find(job.SelectedLanguage)
                ?? throw new InvalidDataException("Requested language is not configured.");
            Saved = saved;
            if (!isPost)
            {
                if (!CanEdit && job.ApprovedSpeech is not null)
                {
                    SpeechGenerationMetadata.Validate(job.ApprovedSpeech, job.SelectedLanguage);
                    if (Translation.Segments.Count != job.ApprovedSpeech.Segments.Count
                        || Translation.Segments.Zip(job.ApprovedSpeech.Segments).Any(pair =>
                            pair.First.Sequence != pair.Second.Sequence || pair.First.Start != pair.Second.Start
                            || pair.First.End != pair.Second.End || pair.First.OriginalText != pair.Second.OriginalText))
                    {
                        throw new InvalidDataException("Approved script no longer matches the original translation.");
                    }
                    foreach (var (segment, approved) in Translation.Segments.Zip(job.ApprovedSpeech.Segments))
                    {
                        segment.EditedText = approved.TranslatedText;
                    }
                }
                Revision = review.Snapshot.Revision;
                Segments = Translation.Segments.Select(segment => new SegmentInput
                {
                    Sequence = segment.Sequence, Text = segment.EffectiveText,
                    UseOriginal = segment.EditedText is null
                }).ToList();
                return Page();
            }
            if (!CanEdit)
            {
                logger.LogWarning("[Job: {JobId}] Script save rejected at status {Status}.", jobId, job.Status);
                return StatusCode(StatusCodes.Status409Conflict, "This job is no longer awaiting script review.");
            }
            if (Segments.Count != Translation.Segments.Count
                || !Segments.Select(segment => segment.Sequence).SequenceEqual(Translation.Segments.Select(segment => segment.Sequence)))
            {
                logger.LogWarning("[Job: {JobId}] Script save rejected: segment identifiers changed.", jobId);
                return BadRequest("Script segment identifiers do not match the saved translation.");
            }
            if (!ModelState.IsValid)
            {
                logger.LogWarning("[Job: {JobId}] Script save rejected: invalid fields.", jobId);
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }
            if (generate)
            {
                if (approval is null || !ApprovalConfirmed)
                {
                    logger.LogWarning("[Job: {JobId}] Full audio generation requires enabled synthesis and explicit approval.", jobId);
                    ModelState.AddModelError(string.Empty, "Confirm approval of the saved script and synthesis charges before generating full audio.");
                    Response.StatusCode = StatusCodes.Status400BadRequest;
                    return Page();
                }
                try
                {
                    await approval.ApproveAsync(job, Language, Revision,
                        Segments.Select((input, index) => input.UseOriginal
                            ? Translation.Segments[index].TranslatedText : input.Text ?? string.Empty).ToArray(),
                        cancellationToken);
                    logger.LogInformation("[Job: {JobId}] Saved script approved; full audio generation queued.", jobId);
                    return RedirectToPage("/Job", new { jobId });
                }
                catch (Exception exception) when (exception is ScriptConflictException or SpeechTimingException)
                {
                    logger.LogWarning("[Job: {JobId}] Audio approval rejected: {Message}", jobId, exception.Message);
                    ModelState.AddModelError(string.Empty, exception.Message);
                    Response.StatusCode = StatusCodes.Status409Conflict;
                    return Page();
                }
            }
            if (previewSequence is not null)
            {
                var index = Translation.Segments.FindIndex(segment => segment.Sequence == previewSequence);
                if (index < 0)
                {
                    logger.LogWarning("[Job: {JobId}] Preview requested for unknown segment {Sequence}.", jobId, previewSequence);
                    return BadRequest("Unknown preview segment.");
                }
                if (previews is null)
                {
                    logger.LogWarning("[Job: {JobId}] Preview requested while synthesis is disabled.", jobId);
                    ModelState.AddModelError(string.Empty, "Voice previews are disabled in configuration.");
                    Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Page();
                }
                var segment = Translation.Segments[index];
                try
                {
                    Preview = await previews.GenerateAsync(job, segment,
                        Segments[index].UseOriginal ? segment.TranslatedText : Segments[index].Text ?? string.Empty,
                        Language, cancellationToken);
                    PreviewSlotSeconds = (segment.End - segment.Start).TotalSeconds;
                }
                catch (SpeechSynthesisException exception)
                {
                    logger.LogWarning(exception, "[Job: {JobId}] Voice preview failed.", jobId);
                    ModelState.AddModelError(string.Empty, exception.Message);
                    Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                }
                catch (VoicePreviewBusyException exception)
                {
                    logger.LogWarning("[Job: {JobId}] Concurrent voice preview rejected.", jobId);
                    ModelState.AddModelError(string.Empty, exception.Message);
                    Response.StatusCode = StatusCodes.Status429TooManyRequests;
                }
                return Page();
            }
            try
            {
                await editor.SaveAsync(job, new ScriptEdits
                {
                    Segments = Segments.ToDictionary(segment => segment.Sequence,
                        segment => segment.UseOriginal ? null : segment.Text ?? string.Empty)
                }, Revision, cancellationToken);
            }
            catch (ScriptConflictException exception)
            {
                logger.LogWarning("[Job: {JobId}] Stale script save rejected.", jobId);
                ModelState.AddModelError(string.Empty, exception.Message);
                Response.StatusCode = StatusCodes.Status409Conflict;
                return Page();
            }
            logger.LogInformation("[Job: {JobId}] Script edits saved without altering the original translation.", jobId);
            return RedirectToPage("/Script", new { jobId, saved = true });
        }
        catch (Exception exception) when (exception is JobStorageException or IOException
            or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            logger.LogError(exception, "[Job: {JobId}] Script storage unavailable or inconsistent.", jobId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Script storage is unavailable or inconsistent. Your changes were not confirmed saved; please contact the administrator.");
        }
    }
}
