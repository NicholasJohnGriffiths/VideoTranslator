using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Tests;

public sealed class CoreModelTests
{
    [Theory]
    [InlineData(null, "Translation")]
    [InlineData("Edited", "Edited")]
    [InlineData("", "")]
    public void EffectiveTextPreservesEditsAndOriginalTranslation(string? editedText, string expected)
    {
        var segment = new VideoSegment { TranslatedText = "Translation", EditedText = editedText };

        Assert.Equal(expected, segment.EffectiveText);
        Assert.Equal("Translation", segment.TranslatedText);
    }

    [Fact]
    public void JobCanFollowTheCompleteWorkflow()
    {
        var job = new VideoJob();
        JobStatus[] workflow =
        [
            JobStatus.ExtractingAudio, JobStatus.AudioReady, JobStatus.Transcribing, JobStatus.TranscriptReady, JobStatus.Translating,
            JobStatus.AwaitingScriptReview, JobStatus.GeneratingSpeech, JobStatus.TranslatedAudioReady, JobStatus.CreatingSubtitles,
            JobStatus.RenderingVideo, JobStatus.Completed
        ];

        foreach (var status in workflow)
        {
            job.TransitionTo(status);
            Assert.Equal(status, job.Status);
        }

        Assert.NotNull(job.CompletedUtc);
        Assert.Null(job.ErrorMessage);
        Assert.Throws<InvalidOperationException>(() => job.TransitionTo(JobStatus.ExtractingAudio));
    }

    [Fact]
    public void JobCannotSkipStagesOrFailWithoutAMessage()
    {
        var job = new VideoJob();
        Assert.Throws<InvalidOperationException>(() => job.TransitionTo(JobStatus.Translating));
        Assert.Throws<ArgumentException>(() => job.TransitionTo(JobStatus.Failed));
        Assert.Equal(JobStatus.Uploaded, job.Status);
    }

    [Fact]
    public void FailedStateSurvivesJsonRoundTrip()
    {
        var job = new VideoJob();
        job.TransitionTo(JobStatus.Failed, "Transcription failed.");

        var restored = JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(job));

        Assert.NotNull(restored);
        Assert.Equal(JobStatus.Failed, restored.Status);
        Assert.Equal(job.CompletedUtc, restored.CompletedUtc);
        Assert.Equal("Transcription failed.", restored.ErrorMessage);
        Assert.Throws<InvalidOperationException>(() => restored.TransitionTo(JobStatus.ExtractingAudio));
    }
}
