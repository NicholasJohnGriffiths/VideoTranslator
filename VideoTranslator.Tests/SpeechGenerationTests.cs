using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class SpeechGenerationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"VideoTranslator-generation-tests-{Guid.NewGuid():N}");
    private static readonly CancellationToken None = CancellationToken.None;
    public SpeechGenerationTests() => Directory.CreateDirectory(directory);
    private static LanguageOption Language() => new()
    {
        Code = "es-ES", SpeechLocale = "es-ES", VoiceName = "es-ES-ElviraNeural"
    };
    private static Transcript Source() => new()
    {
        Segments = [
            new() { Sequence = 1, Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), OriginalText = "First" },
            new() { Sequence = 2, Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4), OriginalText = "Second" }
        ]
    };
    private static Translation Translation() => new()
    {
        TargetLanguage = "es-ES", Segments = Source().Segments.Select(segment => new VideoSegment
        {
            Sequence = segment.Sequence, Start = segment.Start, End = segment.End,
            OriginalText = segment.OriginalText, TranslatedText = $"Spanish {segment.Sequence}"
        }).ToList()
    };
    private static SpeechGeneration Script(bool blankSecond = false) => new()
    {
        Language = Language(), Segments = Translation().Segments.Select(segment => new VideoSegment
        {
            Sequence = segment.Sequence, Start = segment.Start, End = segment.End, OriginalText = segment.OriginalText,
            TranslatedText = blankSecond && segment.Sequence == 2 ? "" : segment.TranslatedText
        }).ToList()
    };
    private static VideoJob ReviewJob()
    {
        var job = new VideoJob { SelectedLanguage = "es-ES", FileSizeBytes = 16 };
        foreach (var status in new[] { JobStatus.ExtractingAudio, JobStatus.AudioReady, JobStatus.Transcribing,
            JobStatus.TranscriptReady, JobStatus.Translating, JobStatus.AwaitingScriptReview }) { job.TransitionTo(status); }
        return job;
    }

    [Theory]
    [InlineData(0.5, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(1.1, 1, 1.1)]
    [InlineData(1.25, 1, 1.25)]
    public void SpeedPolicyVerifiesExactThreshold(double source, double slot, double speed) =>
        Assert.Equal(speed, FFmpegSpeechTimingService.RequiredSpeed(source, slot, 1.25), 8);

    [Theory]
    [InlineData(1.250001, 1)]
    [InlineData(10, 1)]
    public void ExcessiveAccelerationIsExplicitlyRejected(double source, double slot) =>
        Assert.Throws<SpeechTimingException>(() => FFmpegSpeechTimingService.RequiredSpeed(source, slot, 1.25));

    [Theory]
    [InlineData(1.63)]
    [InlineData(1.75)]
    [InlineData(1.96)]
    [InlineData(2.00)]
    public void IncreasedPolicyAcceptsRequiredSpeedUpToExactLimit(double speed) =>
        Assert.Equal(speed, FFmpegSpeechTimingService.RequiredSpeed(speed, 1, SpeechGeneration.NewApprovalMaximumSpeed), 8);

    [Fact]
    public void IncreasedPolicyStillRejectsSpeechAboveTheLimit() =>
        Assert.Throws<SpeechTimingException>(() =>
            FFmpegSpeechTimingService.RequiredSpeed(2.000001, 1, SpeechGeneration.NewApprovalMaximumSpeed));

    [Fact]
    public void PreviousApprovalKeepsItsFrozenLimit()
    {
        var original = new SpeechGeneration { Language = Language(), MaximumSpeed = 1.75, Segments = Script().Segments };
        var restored = JsonSerializer.Deserialize<SpeechGeneration>(JsonSerializer.Serialize(original))!;
        SpeechGenerationMetadata.Validate(restored, "es-ES");
        Assert.Equal(1.75, restored.MaximumSpeed);
        Assert.Throws<SpeechTimingException>(() =>
            FFmpegSpeechTimingService.RequiredSpeed(1.96, 1, restored.MaximumSpeed));
    }

    [Fact]
    public void LegacyApprovalLimitSurvivesSerializationAndMissingField()
    {
        var original = Script();
        var restored = JsonSerializer.Deserialize<SpeechGeneration>(JsonSerializer.Serialize(original))!;
        Assert.Equal(1.25, restored.MaximumSpeed);
        SpeechGenerationMetadata.Validate(restored, "es-ES");
        Assert.Throws<SpeechTimingException>(() =>
            FFmpegSpeechTimingService.RequiredSpeed(1.63, 1, restored.MaximumSpeed));
        Assert.Equal(1.25, JsonSerializer.Deserialize<SpeechGeneration>("{}")!.MaximumSpeed);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.PositiveInfinity)]
    public void InvalidTimingDurationsAreRejected(double source, double slot) =>
        Assert.Throws<InvalidDataException>(() => FFmpegSpeechTimingService.RequiredSpeed(source, slot, 1.25));

    [Fact]
    public void TimelineRejectsOverlapsAndOutOfVideoSlots()
    {
        FFmpegSpeechTimingService.ValidateTimeline(Script(), 5);
        Assert.Throws<SpeechTimingException>(() => FFmpegSpeechTimingService.ValidateTimeline(Script(), 3.9));
        var overlapping = new SpeechGeneration
        {
            Language = Language(), Segments = [Script().Segments[0], new VideoSegment
            {
                Sequence = 2, Start = TimeSpan.FromSeconds(1.5), End = TimeSpan.FromSeconds(3), OriginalText = "second",
                TranslatedText = "spoken"
            }]
        };
        Assert.Throws<SpeechTimingException>(() => FFmpegSpeechTimingService.ValidateTimeline(overlapping, 5));
    }

    [Fact]
    public void InvalidResultAndSpeedPolicyCannotBeAccepted()
    {
        Assert.Throws<InvalidDataException>(() => FFmpegSpeechTimingService.RequiredSpeed(1, 1, double.NaN));
        Assert.Throws<InvalidDataException>(() => FFmpegSpeechTimingService.RequiredSpeed(1, 1, 2.000001));
        var script = Script();
        var invalid = new TimedAudioResult
        {
            RequestId = script.RequestId, DurationSeconds = 5,
            Segments = [new(1, 1, double.NaN, 1), new(2, 1, 1, 1)]
        };
        Assert.Throws<InvalidDataException>(() => SpeechGenerationMetadata.ValidateResult(invalid, script));
        var malformed = JsonSerializer.Deserialize<SpeechGeneration>("""{"language":null}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Throws<InvalidDataException>(() => SpeechGenerationMetadata.ValidateResult(invalid, malformed));
        Assert.Throws<InvalidDataException>(() => FFmpegSpeechTimingService.ValidateTimeline(malformed, 5));
    }

    [Fact]
    public async Task TimingPadsSilencePreservesTotalDurationAndUsesSafePitchPreservingFilter()
    {
        var runner = new Runner();
        var first = Path.Combine(directory, "raw with spaces.wav");
        await File.WriteAllBytesAsync(first, VoicePreviewTests.Wave());
        runner.Durations[first] = 1.1;
        var inspector = Inspector(runner);
        var service = new FFmpegSpeechTimingService(runner, inspector, Options.Create(new MediaOptions()));
        var result = await service.SynchronizeAsync(Script(blankSecond: true), [first, null], 5,
            Path.Combine(directory, "output.wav"), None);
        Assert.Equal(2, result.Count);
        Assert.InRange(result[0].Speed, 1.1, 1.25);
        Assert.Equal(0, result[1].SourceSeconds);
        Assert.Contains(runner.Calls, arguments => arguments.Any(value => value.StartsWith("atempo=")));
        Assert.DoesNotContain(runner.Calls, arguments => arguments.Any(value => value.Contains("asetrate")));
        Assert.All(runner.Calls.Where(arguments => arguments.Contains("-nostdin")), arguments =>
        {
            Assert.Contains("file,pipe", arguments);
            Assert.Contains("1", arguments);
        });
        Assert.Equal(5, await inspector.GetPcmDurationAsync(Path.Combine(directory, "output.wav"), None));
    }

    [Fact]
    public async Task UncleanFitIsRejectedBeforeAnySpeechTrimming()
    {
        var runner = new Runner { FittedDuration = 1.01 };
        var raw = Path.Combine(directory, "raw.wav");
        runner.Durations[raw] = 1.25;
        var service = new FFmpegSpeechTimingService(runner, Inspector(runner), Options.Create(new MediaOptions()));
        await Assert.ThrowsAsync<SpeechTimingException>(() => service.SynchronizeAsync(Script(), [raw, raw], 5,
            Path.Combine(directory, "output.wav"), None));
        Assert.DoesNotContain(runner.Calls, arguments => arguments.Any(value => value.StartsWith("apad=")));
    }

    [Fact]
    public async Task TimingTriesApprovedMaximumBeforeRejectingAnInitiallyUncleanFit()
    {
        var runner = new Runner { FittedDuration = 1.01, MaximumSpeedFittedDuration = 1 };
        var raw = Path.Combine(directory, "raw.wav");
        runner.Durations[raw] = 1.1;
        var service = new FFmpegSpeechTimingService(runner, Inspector(runner), Options.Create(new MediaOptions()));
        var result = await service.SynchronizeAsync(Script(blankSecond: true), [raw, null], 5,
            Path.Combine(directory, "output.wav"), None);
        Assert.Equal(1.25, result[0].Speed);
        Assert.Equal(2, runner.Calls.Count(arguments => arguments.Any(value => value.StartsWith("atempo="))));
    }

    [Fact]
    public async Task ApprovalRejectsUnsavedAndStaleScriptsThenFreezesEffectiveTextAndVoice()
    {
        var state = new State(ReviewJob());
        var editor = new ScriptEditingService(state, state);
        var service = new SpeechGenerationApprovalService(state, editor);
        await Assert.ThrowsAsync<SpeechTimingException>(() => service.ApproveAsync(state.Clone(), Language(), null, ["unsaved", "Spanish 2"], None));
        await Assert.ThrowsAsync<ScriptConflictException>(() => service.ApproveAsync(state.Clone(), Language(), "stale", ["Spanish 1", "Spanish 2"], None));
        state.Edits = new(new ScriptEdits { Segments = new() { [1] = "saved edit", [2] = "" } }, "revision");
        await service.ApproveAsync(state.Clone(), Language(), "revision", ["saved edit", ""], None);
        Assert.Equal(JobStatus.GeneratingSpeech, state.Job.Status);
        Assert.NotNull(state.Job.ApprovedSpeech);
        Assert.Equal(2.00, state.Job.ApprovedSpeech.MaximumSpeed);
        Assert.True(state.Job.ApprovedSpeech.UseAvailableGaps);
        Assert.Equal("saved edit", state.Job.ApprovedSpeech.Segments[0].TranslatedText);
        Assert.Equal("", state.Job.ApprovedSpeech.Segments[1].TranslatedText);
        Assert.Equal("es-ES-ElviraNeural", state.Job.ApprovedSpeech.Language.VoiceName);
        Assert.Equal("revision", state.Job.ApprovedSpeech.ScriptRevision);
        state.Edits = new(new ScriptEdits { Segments = new() { [1] = "later edit", [2] = null } }, "later");
        Assert.Equal("saved edit", state.Clone().ApprovedSpeech!.Segments[0].TranslatedText);
    }

    [Fact]
    public async Task EntireBlankScriptCannotBeApproved()
    {
        var state = new State(ReviewJob())
        {
            Edits = new(new ScriptEdits { Segments = new() { [1] = "", [2] = " " } }, "revision")
        };
        var service = new SpeechGenerationApprovalService(state, new ScriptEditingService(state, state));
        await Assert.ThrowsAsync<SpeechTimingException>(() => service.ApproveAsync(state.Clone(), Language(), "revision", ["", " "], None));
        Assert.Equal(JobStatus.AwaitingScriptReview, state.Job.Status);
    }

    [Fact]
    public void ReviewReturnAndFrozenRequestSurviveRestart()
    {
        var job = ReviewJob();
        job.ApproveSpeech(Script());
        var restored = JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(job))!;
        Assert.Equal(job.ApprovedSpeech!.RequestId, restored.ApprovedSpeech!.RequestId);
        restored.ReturnToScriptReview("Segment 1 is too long.");
        Assert.Equal(JobStatus.AwaitingScriptReview, restored.Status);
        Assert.Null(restored.ApprovedSpeech);
        Assert.Equal("Segment 1 is too long.", restored.ReviewMessage);
        restored.ApproveSpeech(Script());
        Assert.Null(restored.ReviewMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputStorageCommitsAudioBeforeMetadataAndRestoresAcrossInstances(bool blob)
    {
        using var harness = new BlobStorageTests.BlobHarness();
        var script = Script();
        var id = Guid.NewGuid().ToString("N");
        var result = Result(script);
        var raw = Path.Combine(directory, "output.wav");
        await File.WriteAllBytesAsync(raw, VoicePreviewTests.Wave());
        IGeneratedAudioStorageService store = blob ? new BlobGeneratedAudioStorageService(harness.Container) : Local();
        Assert.Null(await store.GetResultAsync(id, script.RequestId, None));
        await store.SaveAsync(id, result, raw, None);
        var restored = await (blob ? new BlobGeneratedAudioStorageService(harness.Container) : Local())
            .GetResultAsync(id, script.RequestId, None);
        Assert.NotNull(restored);
        Assert.Equal(script.RequestId, restored.RequestId);
        using var audio = new MemoryStream();
        await store.CopyAudioAsync(id, script.RequestId, audio, None);
        Assert.Equal(VoicePreviewTests.Wave(), audio.ToArray());
        var competingAudio = VoicePreviewTests.Wave();
        competingAudio[^1] ^= 1;
        await File.WriteAllBytesAsync(raw, competingAudio);
        var conflict = await Assert.ThrowsAsync<JobStorageException>(() => store.SaveAsync(id, result, raw, None));
        Assert.Contains("not overwritten", conflict.Message);
        using var preserved = new MemoryStream();
        await store.CopyAudioAsync(id, script.RequestId, preserved, None);
        Assert.Equal(audio.ToArray(), preserved.ToArray());
        if (blob)
        {
            Assert.EndsWith("/translated-audio.wav", harness.Handler.Writes[0]);
            Assert.EndsWith("/timing.json", harness.Handler.Writes[1]);
            harness.Handler.MissingContainer = true;
            await Assert.ThrowsAsync<JobStorageException>(() => store.GetResultAsync(id, script.RequestId, None));
        }
    }

    [Fact]
    public async Task ProcessorPersistsBeforeReadySkipsBlankSegmentsAndRecoversWithoutSynthesizing()
    {
        var job = ReviewJob(); job.ApproveSpeech(Script(blankSecond: true));
        var state = new State(job);
        var speech = new SpeechStub();
        var runner = new Runner();
        await Processor(state, speech, runner).ProcessAsync(state.Clone(), None);
        Assert.Equal(JobStatus.TranslatedAudioReady, state.Job.Status);
        Assert.Equal(new[] { "audio", "TranslatedAudioReady" }, state.Events);
        Assert.Equal(1, speech.Calls);
        Assert.NotNull(state.Result);
        Assert.Equal(0, state.Result.Segments[1].SourceSeconds);
        var interrupted = ReviewJob(); interrupted.ApproveSpeech(job.ApprovedSpeech!);
        var recovery = new State(interrupted) { Result = state.Result };
        await Processor(recovery, speech, runner).ProcessAsync(recovery.Clone(), None);
        Assert.Equal(JobStatus.TranslatedAudioReady, recovery.Job.Status);
        Assert.Equal(1, speech.Calls);
    }

    [Fact]
    public async Task ProcessorReturnsTimingIssueToReviewAndDoesNotMarkStorageFailuresSuccessful()
    {
        var job = ReviewJob(); job.ApproveSpeech(Script());
        var state = new State(job);
        await Processor(state, new SpeechStub { Samples = 24000 }, new Runner()).ProcessAsync(state.Clone(), None);
        Assert.Equal(JobStatus.AwaitingScriptReview, state.Job.Status);
        Assert.Contains("Segment 1", state.Job.ReviewMessage);
        Assert.Contains("Segment 2", state.Job.ReviewMessage);
        Assert.Equal(new[] { 1, 2 }, state.Job.ReviewTimingIssues.Select(issue => issue.Sequence));
        Assert.Null(state.Result);
        var pending = ReviewJob(); pending.ApproveSpeech(Script(blankSecond: true));
        var failure = new State(pending) { FailSaving = true };
        await Assert.ThrowsAsync<JobStorageException>(() => Processor(failure, new SpeechStub(), new Runner()).ProcessAsync(failure.Clone(), None));
        Assert.Equal(JobStatus.GeneratingSpeech, failure.Job.Status);
        Assert.Null(failure.Result);
    }

    [Fact]
    public async Task ProcessorChecksAllSegmentsCachesFailuresAndClearsReportOnReapproval()
    {
        var job = ReviewJob(); job.ApproveSpeech(Script());
        var state = new State(job);
        var speech = new SpeechStub { Samples = 24000 };
        var runner = new Runner();
        await Processor(state, speech, runner).ProcessAsync(state.Clone(), None);
        Assert.Equal(2, speech.Calls);
        Assert.Equal(2, state.Job.ReviewTimingIssues.Count);
        Assert.Contains("Timing checked for all 2 segments", state.Job.ReviewMessage);
        Assert.DoesNotContain(runner.Calls, arguments => arguments.Contains("-af"));
        Assert.DoesNotContain("audio", state.Events);
        var restored = state.Clone();
        Assert.Equal(state.Job.ReviewTimingIssues, restored.ReviewTimingIssues);
        restored.ApproveSpeech(new SpeechGeneration
        {
            Language = Language(), MaximumSpeed = 2, Segments = Script().Segments
        });
        Assert.Null(restored.ReviewMessage);
        Assert.Empty(restored.ReviewTimingIssues);
        await state.UpdateAsync(restored, JobStatus.AwaitingScriptReview, None);
        await Processor(state, speech, new Runner()).ProcessAsync(state.Clone(), None);
        Assert.Equal(2, speech.Calls);
        Assert.Equal(JobStatus.TranslatedAudioReady, state.Job.Status);
        Assert.Empty(state.Job.ReviewTimingIssues);
    }

    [Fact]
    public async Task TimingReportOmitsBlankAndFittingSegments()
    {
        var script = new SpeechGeneration
        {
            Language = Language(), Segments =
            [
                Script().Segments[0],
                new() { Sequence = 2, Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(4),
                    OriginalText = "Second", TranslatedText = "Fits" },
                new() { Sequence = 3, Start = TimeSpan.FromSeconds(4), End = TimeSpan.FromSeconds(5),
                    OriginalText = "Third", TranslatedText = "" }
            ]
        };
        var job = ReviewJob(); job.ApproveSpeech(script);
        var state = new State(job);
        var speech = new SpeechStub { Samples = 24000 };
        await Processor(state, speech, new Runner()).ProcessAsync(state.Clone(), None);
        Assert.Equal(2, speech.Calls);
        Assert.Equal(1, Assert.Single(state.Job.ReviewTimingIssues).Sequence);
        Assert.Contains("all 3 segments", state.Job.ReviewMessage);
        Assert.Null(state.Result);
    }

    [Fact]
    public async Task AvailableGapsKeepNaturalSpeechAndPadUntilNextStartAndVideoEnd()
    {
        var script = new SpeechGeneration
        {
            Language = Language(), MaximumSpeed = 2, UseAvailableGaps = true, Segments = Script().Segments
        };
        var runner = new Runner();
        var first = Path.Combine(directory, "first.wav");
        var last = Path.Combine(directory, "last.wav");
        runner.Durations[first] = 1.5;
        runner.Durations[last] = 1.8;
        var service = new FFmpegSpeechTimingService(runner, Inspector(runner), Options.Create(new MediaOptions()));
        var timings = await service.SynchronizeAsync(script, [first, last], 5,
            Path.Combine(directory, "output.wav"), None);
        Assert.All(timings, timing =>
        {
            Assert.Equal(2, timing.SlotSeconds);
            Assert.Equal(1, timing.Speed);
        });
        Assert.DoesNotContain(runner.Calls, args => args.Any(arg => arg.StartsWith("atempo=")));
        Assert.Equal(2, runner.Calls.Count(args => args.Contains("apad=whole_len=32000,atrim=end_sample=32000")));
        SpeechGenerationMetadata.ValidateResult(new TimedAudioResult
        {
            RequestId = script.RequestId, DurationSeconds = 5, Segments = timings.ToList()
        }, script);
    }

    [Theory]
    [InlineData(2.54, false)]
    [InlineData(4.0001, true)]
    public async Task GapWindowAcceleratesOnlyWhenNecessaryAndRejectsOverlap(double seconds, bool rejected)
    {
        var script = new SpeechGeneration
        {
            Language = Language(), MaximumSpeed = 2, UseAvailableGaps = true, Segments = Script(true).Segments
        };
        var runner = new Runner();
        var raw = Path.Combine(directory, "gap.wav");
        runner.Durations[raw] = seconds;
        var service = new FFmpegSpeechTimingService(runner, Inspector(runner), Options.Create(new MediaOptions()));
        if (rejected)
        {
            await Assert.ThrowsAsync<SpeechTimingException>(() =>
                service.SynchronizeAsync(script, [raw, null], 5, Path.Combine(directory, "output.wav"), None));
            Assert.DoesNotContain(runner.Calls, args => args.Any(arg => arg.StartsWith("apad=")));
        }
        else
        {
            var result = await service.SynchronizeAsync(script, [raw, null], 5, Path.Combine(directory, "output.wav"), None);
            Assert.InRange(result[0].Speed, 1.27, 1.28);
            Assert.Equal(2, result[0].SlotSeconds);
            Assert.Equal(0, result[1].SourceSeconds);
        }
    }

    [Fact]
    public async Task ProcessorUsesGapPolicyForPreflightAndAssembly()
    {
        var job = ReviewJob();
        job.ApproveSpeech(new SpeechGeneration
        {
            Language = Language(), MaximumSpeed = 2, UseAvailableGaps = true, Segments = Script().Segments
        });
        var state = new State(job);
        var runner = new Runner();
        await Processor(state, new SpeechStub { Samples = 40640 }, runner).ProcessAsync(state.Clone(), None);
        Assert.Equal(JobStatus.TranslatedAudioReady, state.Job.Status);
        Assert.All(state.Result!.Segments, timing => Assert.Equal(2, timing.SlotSeconds));
        Assert.Empty(state.Job.ReviewTimingIssues);
    }

    [Fact]
    public void GapPolicyIsFrozenAndLegacyTimingStillValidates()
    {
        var old = Script();
        Assert.False(JsonSerializer.Deserialize<SpeechGeneration>("{}")!.UseAvailableGaps);
        Assert.False(JsonSerializer.Deserialize<SpeechGeneration>(JsonSerializer.Serialize(old))!.UseAvailableGaps);
        SpeechGenerationMetadata.ValidateResult(Result(old), old);
        var current = new SpeechGeneration
        {
            Language = Language(), UseAvailableGaps = true, Segments = old.Segments
        };
        Assert.True(JsonSerializer.Deserialize<SpeechGeneration>(JsonSerializer.Serialize(current))!.UseAvailableGaps);
        Assert.Throws<InvalidDataException>(() => SpeechGenerationMetadata.ValidateResult(Result(current), current));
        Assert.Equal(2, SpeechTimingWindow.SlotSeconds(current, 0, 5));
        Assert.Equal(2, SpeechTimingWindow.SlotSeconds(current, 1, 5));
        var adjacent = new SpeechGeneration
        {
            UseAvailableGaps = true, Segments =
            [
                new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) },
                new() { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2) }
            ]
        };
        Assert.Equal(1, SpeechTimingWindow.SlotSeconds(adjacent, 0, 2));
        Assert.Equal(1, SpeechTimingWindow.SlotSeconds(adjacent, 1, 2));
    }

    [Fact]
    public async Task ProcessorPersistsSynthesisFailureAndCancellationLeavesRecoverableStatus()
    {
        var job = ReviewJob(); job.ApproveSpeech(Script());
        var state = new State(job);
        await Processor(state, new SpeechStub { Failure = new SpeechSynthesisException("Speech unavailable.") }, new Runner())
            .ProcessAsync(state.Clone(), None);
        Assert.Equal(JobStatus.Failed, state.Job.Status);
        Assert.Equal(JobStatus.GeneratingSpeech, state.Job.FailedAtStatus);
        var pending = ReviewJob(); pending.ApproveSpeech(Script());
        var cancelled = new State(pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Processor(cancelled, new SpeechStub { Failure = new OperationCanceledException() }, new Runner()).ProcessAsync(cancelled.Clone(), None));
        Assert.Equal(JobStatus.GeneratingSpeech, cancelled.Job.Status);
    }

    [Fact]
    public async Task ChangedSourceVideoFailsJobWithoutStoppingWorker()
    {
        var job = ReviewJob(); job.ApproveSpeech(Script());
        var state = new State(job) { DownloadFailure = new UploadValidationException("The source video size no longer matches.") };
        await Processor(state, new SpeechStub(), new Runner()).ProcessAsync(state.Clone(), None);
        Assert.Equal(JobStatus.Failed, state.Job.Status);
        Assert.Equal(JobStatus.GeneratingSpeech, state.Job.FailedAtStatus);
        Assert.Null(state.Result);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"format":{"duration":"NaN"},"streams":[]}""")]
    [InlineData("""{"format":{"duration":"1"},"streams":[null]}""")]
    [InlineData("""{"format":{"duration":3},"streams":[]}""")]
    public async Task InspectorRejectsMalformedMetadataWithoutCrashingWorker(string json)
    {
        var runner = new Runner { ProbeOverride = json };
        await Assert.ThrowsAsync<InvalidDataException>(() => Inspector(runner).GetPcmDurationAsync("unused.wav", None));
    }

    private IGeneratedAudioStorageService Local() => new LocalGeneratedAudioStorageService(
        new TestEnvironment { ContentRootPath = directory }, Options.Create(new LocalStorageOptions { RootPath = "jobs" }));
    private static MediaAudioInspector Inspector(Runner runner) => new(runner, Options.Create(new MediaOptions()));
    private static TimedAudioResult Result(SpeechGeneration script) => new()
    {
        RequestId = script.RequestId, DurationSeconds = 5,
        Segments = script.Segments.Select(segment => new SegmentAudioTiming(segment.Sequence, 1, 1, 1)).ToList()
    };
    private static SpeechGenerationProcessor Processor(State state, SpeechStub speech, Runner runner)
    {
        var voices = new VoicePreviewService(speech, state, NullLogger<VoicePreviewService>.Instance);
        var inspector = Inspector(runner);
        return new(state, voices, state, state, new FFmpegSpeechTimingService(runner, inspector, Options.Create(new MediaOptions())),
            inspector, NullLogger<SpeechGenerationProcessor>.Instance);
    }

    private sealed class Runner : IMediaProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Dictionary<string, double> Durations { get; } = [];
        public double FittedDuration { get; init; } = 1;
        public double? MaximumSpeedFittedDuration { get; init; }
        public string? ProbeOverride { get; init; }
        public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            var path = arguments[^1];
            if (arguments.Contains("-show_entries"))
            {
                if (ProbeOverride is not null) { return ProbeOverride; }
                var duration = Durations.GetValueOrDefault(path, path.EndsWith(".mp4") || path.EndsWith("translated-audio.wav") ? 5 : 1);
                return JsonSerializer.Serialize(new
                {
                    format = new { duration = duration.ToString(CultureInfo.InvariantCulture) },
                    streams = new[] { new { codec_name = "pcm_s16le", sample_rate = "16000", channels = 1, bits_per_sample = 16 } }
                });
            }
            await File.WriteAllBytesAsync(path, VoicePreviewTests.Wave(), cancellationToken);
            var filter = arguments.FirstOrDefault(value => value.StartsWith("atempo="));
            Durations[path] = filter is not null
                ? filter == "atempo=1.25" ? MaximumSpeedFittedDuration ?? FittedDuration : FittedDuration
                : path.EndsWith("output.wav") || path.EndsWith("translated-audio.wav") ? 5 : 1;
            return "";
        }
    }
    private sealed class SpeechStub : ISpeechSynthesisService
    {
        public int Calls { get; private set; }
        public int Samples { get; init; } = 16000;
        public Exception? Failure { get; init; }
        public async Task<string> GenerateSpeechAsync(string text, LanguageOption language, string outputPath, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) { throw Failure; }
            await File.WriteAllBytesAsync(outputPath, VoicePreviewTests.Wave(Samples), cancellationToken);
            return outputPath;
        }
    }
    private sealed class State(VideoJob job) : IJobStorageService, ITranscriptStorageService, ITranslationStorageService,
        IVoicePreviewStorageService, IGeneratedAudioStorageService
    {
        public VideoJob Job { get; private set; } = job;
        public ScriptEditSnapshot Edits { get; set; } = new(new ScriptEdits(), null);
        public TimedAudioResult? Result { get; set; }
        public bool FailSaving { get; init; }
        public Exception? DownloadFailure { get; init; }
        public List<string> Events { get; } = [];
        private readonly Dictionary<string, byte[]> raw = [];
        public VideoJob Clone() => JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(Job))!;
        public Task CreateAsync(VideoJob value, Stream stream, CancellationToken token) => throw new NotSupportedException();
        public Task<VideoJob?> GetAsync(string id, CancellationToken token) => Task.FromResult<VideoJob?>(Clone());
        public IAsyncEnumerable<string> ListJobIdsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task DownloadVideoAsync(VideoJob value, string path, CancellationToken token) =>
            DownloadFailure is not null ? Task.FromException(DownloadFailure) : File.WriteAllBytesAsync(path, [1], token);
        public Task SaveAudioAsync(string id, string path, CancellationToken token) => throw new NotSupportedException();
        public Task UpdateAsync(VideoJob value, JobStatus expected, CancellationToken token)
        {
            Assert.Equal(expected, Job.Status); Job = JsonSerializer.Deserialize<VideoJob>(JsonSerializer.Serialize(value))!;
            Events.Add(value.Status.ToString()); return Task.CompletedTask;
        }
        public Task DownloadAudioAsync(string id, string path, CancellationToken token) => throw new NotSupportedException();
        public Task SaveTranscriptAsync(string id, Transcript transcript, CancellationToken token) => throw new NotSupportedException();
        public Task<Transcript?> GetTranscriptAsync(string id, CancellationToken token) => Task.FromResult<Transcript?>(Source());
        public Task<Translation?> GetTranslationAsync(string id, CancellationToken token) => Task.FromResult<Translation?>(Translation());
        public Task SaveTranslationAsync(string id, Translation value, CancellationToken token) => throw new NotSupportedException();
        public Task<ScriptEditSnapshot> GetEditsAsync(string id, CancellationToken token) => Task.FromResult(Edits);
        public Task SaveEditsAsync(string id, ScriptEdits edits, string? revision, CancellationToken token) => throw new NotSupportedException();
        public Task<byte[]?> GetAsync(string id, int sequence, string key, CancellationToken token) => Task.FromResult(raw.GetValueOrDefault(key));
        public Task SaveAsync(string id, int sequence, string key, byte[] bytes, CancellationToken token)
        {
            raw.Add(key, bytes); return Task.CompletedTask;
        }
        public Task<TimedAudioResult?> GetResultAsync(string id, string requestId, CancellationToken token) => Task.FromResult(Result);
        public Task SaveAsync(string id, TimedAudioResult result, string path, CancellationToken token)
        {
            if (FailSaving) { throw new JobStorageException("Unavailable", new IOException()); }
            Result = result; Events.Add("audio"); return Task.CompletedTask;
        }
        public Task CopyAudioAsync(string id, string requestId, Stream stream, CancellationToken token) =>
            stream.WriteAsync(VoicePreviewTests.Wave(), token).AsTask();
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "GenerationTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
