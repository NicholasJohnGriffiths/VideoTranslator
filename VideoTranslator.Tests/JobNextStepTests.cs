using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Tests;

public sealed class JobNextStepTests
{
    private static VideoJob Job(JobStatus status, string? message = null) =>
        JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(new { Status = status, ReviewMessage = message }))!;

    [Theory]
    [InlineData(JobStatus.Uploaded)]
    [InlineData(JobStatus.ExtractingAudio)]
    [InlineData(JobStatus.AudioReady)]
    [InlineData(JobStatus.Transcribing)]
    [InlineData(JobStatus.TranscriptReady)]
    [InlineData(JobStatus.Translating)]
    [InlineData(JobStatus.GeneratingSpeech)]
    [InlineData(JobStatus.CreatingSubtitles)]
    [InlineData(JobStatus.RenderingVideo)]
    public void AutomaticStagesTellUsersToWaitWithoutApproval(JobStatus status)
    {
        var next = JobNextStep.For(Job(status), true, true, true, true);
        Assert.StartsWith("Please wait", next.Instruction);
        Assert.Contains("job page refreshes automatically", next.Instruction);
        Assert.Null(next.Page);
    }

    [Theory]
    [InlineData(JobStatus.AwaitingScriptReview, "/Script")]
    [InlineData(JobStatus.TranslatedAudioReady, "/TranslatedAudio")]
    [InlineData(JobStatus.Completed, "/Result")]
    public void UserActionStagesLinkToCorrectPageEvenWhenWorkerPaused(JobStatus status, string page)
    {
        var next = JobNextStep.For(Job(status), true, true, true, false);
        Assert.Equal(page, next.Page);
        Assert.NotNull(next.Action);
        Assert.DoesNotContain("Please wait", next.Instruction);
    }

    [Fact]
    public void ReturnedReviewExplainsTimingWarnings()
    {
        var next = JobNextStep.For(Job(JobStatus.AwaitingScriptReview, "Too long"), true, true, true, true);
        Assert.Contains("timing warnings", next.Instruction);
        Assert.Equal("/Script", next.Page);
    }

    [Fact]
    public void DisabledSynthesisAllowsReviewWithoutPromisingGeneration()
    {
        var next = JobNextStep.For(Job(JobStatus.AwaitingScriptReview), true, true, false, true);
        Assert.Contains("audio generation is disabled", next.Instruction);
        Assert.Equal("Review script", next.Action);
    }

    [Theory]
    [InlineData(JobStatus.Uploaded, true, true, true, false)]
    [InlineData(JobStatus.AudioReady, false, true, true, true)]
    [InlineData(JobStatus.Transcribing, false, true, true, true)]
    [InlineData(JobStatus.TranscriptReady, true, false, true, true)]
    [InlineData(JobStatus.Translating, true, false, true, true)]
    [InlineData(JobStatus.GeneratingSpeech, true, true, false, true)]
    public void DisabledProcessingDoesNotTellUsersToWaitForAutomaticWork(
        JobStatus status, bool transcription, bool translation, bool synthesis, bool worker)
    {
        var next = JobNextStep.For(Job(status), transcription, translation, synthesis, worker);
        Assert.Contains("paused", next.Instruction);
        Assert.Null(next.Page);
    }

    [Fact]
    public void FailureRequiresAdministratorRatherThanApproval()
    {
        var next = JobNextStep.For(Job(JobStatus.Failed), true, true, true, true);
        Assert.Contains("contact the administrator", next.Instruction);
        Assert.Null(next.Page);
    }
}
