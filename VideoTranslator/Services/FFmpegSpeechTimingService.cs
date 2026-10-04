using System.Globalization;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class FFmpegSpeechTimingService(
    IMediaProcessRunner runner, MediaAudioInspector inspector, IOptions<MediaOptions> options) : ISpeechTimingService
{
    public static double RequiredSpeed(double sourceSeconds, double slotSeconds, double maximumSpeed)
    {
        if (!double.IsFinite(sourceSeconds) || sourceSeconds <= 0 || !double.IsFinite(slotSeconds) || slotSeconds <= 0
            || !double.IsFinite(maximumSpeed) || maximumSpeed is < 1 or > 1.25)
        {
            throw new InvalidDataException("Speech timing requires positive finite durations and a speed limit between 1 and 1.25.");
        }
        var speed = Math.Max(1, sourceSeconds / slotSeconds);
        if (speed > maximumSpeed)
        {
            throw new SpeechTimingException(
                $"Speech needs {speed:0.00}x speed, exceeding the {maximumSpeed:0.00}x limit. Shorten this segment's wording.");
        }
        return speed;
    }

    public static void ValidateTimeline(SpeechGeneration script, double videoSeconds)
    {
        SpeechGenerationMetadata.Validate(script, script.Language?.Code ?? string.Empty);
        if (!double.IsFinite(videoSeconds) || videoSeconds <= 0 || videoSeconds >= 5 * 60 * 60)
        {
            throw new SpeechTimingException("Video duration must be positive and shorter than five hours.");
        }
        long previousEnd = 0;
        foreach (var segment in script.Segments)
        {
            var start = Samples(segment.Start.TotalSeconds);
            var end = Samples(segment.End.TotalSeconds);
            if (start < previousEnd)
            {
                throw new SpeechTimingException($"Segment {segment.Sequence} overlaps the preceding segment. Overlapping-speaker dubbing is not supported yet; review the transcript rather than merging or shifting timings.");
            }
            if (end <= start || end > Samples(videoSeconds))
            {
                throw new SpeechTimingException($"Segment {segment.Sequence} has an invalid time slot or extends beyond the video.");
            }
            previousEnd = end;
        }
    }

    public async Task<IReadOnlyList<SegmentAudioTiming>> SynchronizeAsync(
        SpeechGeneration script, IReadOnlyList<string?> audioPaths, double videoDurationSeconds,
        string outputPath, CancellationToken cancellationToken)
    {
        ValidateTimeline(script, videoDurationSeconds);
        if (audioPaths.Count != script.Segments.Count)
        {
            throw new InvalidDataException("Speech files do not match the approved script.");
        }
        var folder = Path.Combine(Path.GetDirectoryName(outputPath)!, $"timing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var clips = new List<string>();
        var timings = new List<SegmentAudioTiming>();
        long cursor = 0;
        foreach (var (segment, index) in script.Segments.Select((segment, index) => (segment, index)))
        {
            var start = Samples(segment.Start.TotalSeconds);
            var end = Samples(segment.End.TotalSeconds);
            if (start > cursor)
            {
                await SilenceAsync(start - cursor, folder, clips, cancellationToken);
            }
            var slot = end - start;
            var raw = audioPaths[index];
            if (string.IsNullOrWhiteSpace(segment.TranslatedText))
            {
                if (raw is not null)
                {
                    throw new InvalidDataException("Blank segments must have no speech audio.");
                }
                await SilenceAsync(slot, folder, clips, cancellationToken);
                timings.Add(new(segment.Sequence, 0, (segment.End - segment.Start).TotalSeconds, 1));
            }
            else
            {
                if (raw is null)
                {
                    throw new InvalidDataException("A spoken segment is missing audio.");
                }
                var rawSeconds = await inspector.GetPcmDurationAsync(raw, cancellationToken);
                var speed = RequiredSpeed(rawSeconds, slot / 16000d, script.MaximumSpeed);
                var fitted = raw;
                if (speed > 1)
                {
                    speed = Math.Min(script.MaximumSpeed, speed + 0.005);
                    fitted = Path.Combine(folder, $"fitted-{index}.wav");
                    await RunAsync(["-i", raw, "-af", $"atempo={Number(speed)}"], fitted, cancellationToken);
                    var fittedSeconds = await inspector.GetPcmDurationAsync(fitted, cancellationToken);
                    if (Samples(fittedSeconds) > slot && speed < script.MaximumSpeed)
                    {
                        speed = script.MaximumSpeed;
                        await RunAsync(["-i", raw, "-af", $"atempo={Number(speed)}"], fitted, cancellationToken);
                        fittedSeconds = await inspector.GetPcmDurationAsync(fitted, cancellationToken);
                    }
                    if (Samples(fittedSeconds) > slot)
                    {
                        throw new SpeechTimingException($"Segment {segment.Sequence} could not fit cleanly within the {script.MaximumSpeed:0.00}x speed limit. Shorten its wording; speech was not cut off.");
                    }
                }
                var clip = Path.Combine(folder, $"{clips.Count:D6}.wav");
                if (Samples(await inspector.GetPcmDurationAsync(fitted, cancellationToken)) > slot)
                {
                    throw new SpeechTimingException($"Segment {segment.Sequence} is longer than its original slot. Shorten its wording; speech was not cut off.");
                }
                await RunAsync(["-i", fitted, "-af", $"apad=whole_len={slot},atrim=end_sample={slot}"], clip, cancellationToken);
                clips.Add(Path.GetFileName(clip));
                timings.Add(new(segment.Sequence, rawSeconds, (segment.End - segment.Start).TotalSeconds, speed));
            }
            cursor = end;
        }
        var totalSamples = Samples(videoDurationSeconds);
        if (cursor < totalSamples)
        {
            await SilenceAsync(totalSamples - cursor, folder, clips, cancellationToken);
        }
        var manifest = Path.Combine(folder, "concat.txt");
        await File.WriteAllLinesAsync(manifest, clips.Select(name => $"file '{name}'"), cancellationToken);
        await RunAsync(["-f", "concat", "-safe", "1", "-i", manifest], outputPath, cancellationToken);
        var duration = await inspector.GetPcmDurationAsync(outputPath, cancellationToken);
        if (Math.Abs(duration - totalSamples / 16000d) > 1d / 16000 + 0.000001)
        {
            throw new InvalidDataException("Assembled audio does not match the source video duration.");
        }
        return timings;
    }

    private async Task SilenceAsync(long samples, string folder, List<string> clips, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, $"{clips.Count:D6}.wav");
        await RunAsync(["-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-af", $"atrim=end_sample={samples}"],
            path, cancellationToken);
        clips.Add(Path.GetFileName(path));
    }

    private Task<string> RunAsync(IReadOnlyList<string> input, string output, CancellationToken cancellationToken) =>
        runner.RunAsync(options.Value.FFmpegPath,
            new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-threads", "1" }
                .Concat(input).Concat(["-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-threads", "1", output]).ToArray(),
            cancellationToken);

    private static long Samples(double seconds) => checked((long)Math.Round(seconds * 16000, MidpointRounding.AwayFromZero));
    private static string Number(double number) => number.ToString("0.########", CultureInfo.InvariantCulture);
}
