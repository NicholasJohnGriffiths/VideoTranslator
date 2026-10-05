using VideoTranslator.Models;

namespace VideoTranslator.Tests;

public sealed class JobStageProgressTests
{
    private static VideoJob At(JobStatus status)
    {
        var job = new VideoJob();
        JobStatus[] sequence = [JobStatus.Uploaded, JobStatus.ExtractingAudio, JobStatus.AudioReady,
            JobStatus.Transcribing, JobStatus.TranscriptReady, JobStatus.Translating,
            JobStatus.AwaitingScriptReview, JobStatus.GeneratingSpeech, JobStatus.TranslatedAudioReady,
            JobStatus.CreatingSubtitles, JobStatus.RenderingVideo, JobStatus.Completed];
        foreach (var next in sequence.Skip(1))
        {
            if (job.Status == status) break;
            job.TransitionTo(next);
        }
        return job;
    }

    [Theory]
    [InlineData(JobStatus.Uploaded, 1, StageProgressState.Waiting)]
    [InlineData(JobStatus.ExtractingAudio, 1, StageProgressState.Running)]
    [InlineData(JobStatus.AudioReady, 2, StageProgressState.Waiting)]
    [InlineData(JobStatus.Transcribing, 2, StageProgressState.Running)]
    [InlineData(JobStatus.TranscriptReady, 3, StageProgressState.Waiting)]
    [InlineData(JobStatus.Translating, 3, StageProgressState.Running)]
    [InlineData(JobStatus.AwaitingScriptReview, 3, StageProgressState.Review)]
    [InlineData(JobStatus.GeneratingSpeech, 4, StageProgressState.Running)]
    [InlineData(JobStatus.TranslatedAudioReady, 5, StageProgressState.Review)]
    [InlineData(JobStatus.CreatingSubtitles, 5, StageProgressState.Running)]
    [InlineData(JobStatus.RenderingVideo, 6, StageProgressState.Running)]
    public void EachStatusReportsOnlyActualCompletedStages(JobStatus status, int current, StageProgressState state)
    {
        var stages = JobStageProgress.For(At(status), true, true, true);
        Assert.Equal(7, stages.Count);
        Assert.All(stages.Take(current), stage =>
        {
            Assert.Equal(StageProgressState.Complete, stage.State);
            Assert.Equal(100, stage.Value);
        });
        Assert.Equal(state, stages[current].State);
        Assert.Equal(state == StageProgressState.Running ? (int?)null : 0, stages[current].Value);
        Assert.All(stages.Skip(current + 1), stage => Assert.Equal(StageProgressState.Waiting, stage.State));
    }

    [Fact]
    public void CompletedJobHasSevenFullBars() =>
        Assert.All(JobStageProgress.For(At(JobStatus.Completed), false, false, false),
            stage => Assert.Equal(100, stage.Value));

    [Theory]
    [InlineData(JobStatus.ExtractingAudio, 1)]
    [InlineData(JobStatus.Transcribing, 2)]
    [InlineData(JobStatus.Translating, 3)]
    [InlineData(JobStatus.GeneratingSpeech, 4)]
    [InlineData(JobStatus.CreatingSubtitles, 5)]
    [InlineData(JobStatus.RenderingVideo, 6)]
    public void FailureMarksItsStageAndStopsLaterStages(JobStatus status, int current)
    {
        var job = At(status);
        job.TransitionTo(JobStatus.Failed, "Failure");
        var stages = JobStageProgress.For(job, true, true, true);
        Assert.All(stages.Take(current), stage => Assert.Equal(StageProgressState.Complete, stage.State));
        Assert.Equal(StageProgressState.Failed, stages[current].State);
        Assert.All(stages.Skip(current + 1), stage => Assert.Equal(StageProgressState.Stopped, stage.State));
        Assert.DoesNotContain(stages, stage => stage.State == StageProgressState.Running);
    }

    [Theory]
    [InlineData(JobStatus.AudioReady, 2)]
    [InlineData(JobStatus.Transcribing, 2)]
    [InlineData(JobStatus.TranscriptReady, 3)]
    [InlineData(JobStatus.Translating, 3)]
    [InlineData(JobStatus.GeneratingSpeech, 4)]
    public void DisabledProcessingIsNotShownAsRunning(JobStatus status, int current)
    {
        var stages = JobStageProgress.For(At(status), false, false, false);
        Assert.Equal(StageProgressState.Paused, stages[current].State);
        Assert.Equal("Disabled", stages[current].Description);
    }

    [Fact]
    public void ReturnedAudioRequiresReviewNotCompletedOrRunning()
    {
        var job = At(JobStatus.GeneratingSpeech);
        job.ReturnToScriptReview("Segment 2 does not fit.");
        var stages = JobStageProgress.For(job, true, true, true);
        Assert.Equal(StageProgressState.Review, stages[3].State);
        Assert.Equal(StageProgressState.Review, stages[4].State);
        Assert.Equal("Returned to script review", stages[4].Description);
        Assert.Equal(0, stages[4].Value);
    }
}
