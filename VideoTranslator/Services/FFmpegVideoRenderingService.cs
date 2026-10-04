using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class FFmpegVideoRenderingService(
    IMediaProcessRunner runner, MediaAudioInspector audio, IOptions<MediaOptions> options) : IVideoRenderingService
{
    public async Task<string> RenderTranslatedVideoAsync(
        string videoPath, string audioPath, string subtitlePath, CancellationToken cancellationToken)
    {
        var source = await ProbeAsync(videoPath, cancellationToken);
        var duration = await audio.GetPcmDurationAsync(audioPath, cancellationToken);
        if (Math.Abs(source.Duration - duration) > 0.001)
        { throw new InvalidDataException("Translated audio duration no longer matches the source video."); }
        var output = Path.Combine(Path.GetDirectoryName(videoPath)!, "translated-video.mp4");
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-xerror",
            "-protocol_whitelist", "file,pipe", "-threads", "1", "-noautorotate", "-i", videoPath,
            "-protocol_whitelist", "file,pipe", "-i", audioPath,
            "-protocol_whitelist", "file,pipe", "-f", "webvtt", "-i", subtitlePath,
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:s:0",
            "-map_metadata", "-1", "-map_chapters", "-1", "-c:v"
        };
        var copy = source.VideoCodec == "h264" && source.PixelFormat == "yuv420p";
        arguments.Add(copy ? "copy" : "libx264");
        if (!copy)
        {
            if (source.Width % 2 != 0 || source.Height % 2 != 0)
            { throw new MediaProcessingException("This video's odd dimensions cannot be encoded to compatible H.264 without changing the picture. Use an even-dimension source."); }
            arguments.AddRange(["-pix_fmt", "yuv420p", "-preset", "medium", "-crf", "20"]);
        }
        arguments.AddRange(["-c:a", "aac", "-b:a", "128k", "-c:s", "mov_text",
            "-disposition:a:0", "default", "-disposition:s:0", "0",
            "-metadata:s:a:0", "title=Translated voice",
            "-metadata:s:s:0", "title=Translated subtitles", "-movflags", "+faststart", "-threads", "1", output]);
        await runner.RunAsync(options.Value.FFmpegPath, arguments, cancellationToken);
        var result = await ProbeAsync(output, cancellationToken);
        ValidateOutput(result, source, duration);
        return output;
    }

    public async Task<double> VerifyAsync(string path, double expectedDuration, CancellationToken token)
    {
        var result = await ProbeAsync(path, token);
        ValidateOutput(result, result, expectedDuration);
        return result.Duration;
    }

    private static void ValidateOutput(VideoProbe result, VideoProbe source, double expected)
    {
        if (result.VideoCount != 1 || result.AudioCount != 1 || result.SubtitleCount != 1
            || result.TotalStreams != 3 || result.VideoCodec != "h264" || result.PixelFormat != "yuv420p"
            || result.AudioCodec != "aac" || result.SubtitleCodec != "mov_text"
            || result.Width != source.Width || result.Height != source.Height
            || Math.Abs(Math.IEEERemainder(result.Rotation - source.Rotation, 360)) > 0.01
            || Math.Abs(result.Duration - expected) > 0.15)
        { throw new InvalidDataException("Rendered MP4 tracks, picture dimensions or duration are invalid."); }
    }

    private async Task<VideoProbe> ProbeAsync(string path, CancellationToken token)
    {
        var json = await runner.RunAsync(options.Value.FFprobePath,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
                "format=duration:stream=codec_type,codec_name,pix_fmt,width,height:stream_side_data=rotation", "-of", "json", path], token);
        return ParseProbe(json);
    }

    internal static VideoProbe ParseProbe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.Object || !format.TryGetProperty("duration", out var duration)
            || duration.ValueKind != JsonValueKind.String
            || !double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds <= 0 || seconds >= 18000
            || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        { throw new InvalidDataException("Video probe metadata is invalid."); }
        var videos = streams.EnumerateArray().Where(value => Text(value, "codec_type") == "video").ToArray();
        var audios = streams.EnumerateArray().Where(value => Text(value, "codec_type") == "audio").ToArray();
        var subtitles = streams.EnumerateArray().Where(value => Text(value, "codec_type") == "subtitle").ToArray();
        if (videos.Length == 0 || Number(videos[0], "width") <= 0 || Number(videos[0], "height") <= 0)
        { throw new InvalidDataException("Video picture metadata is invalid."); }
        return new(seconds, Text(videos[0], "codec_name"), Text(videos[0], "pix_fmt"),
            Number(videos[0], "width"), Number(videos[0], "height"), Rotation(videos[0]),
            audios.Length == 0 ? "" : Text(audios[0], "codec_name"),
            subtitles.Length == 0 ? "" : Text(subtitles[0], "codec_name"),
            videos.Length, audios.Length, subtitles.Length, streams.GetArrayLength());
    }
    private static string Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()! : "";
    private static int Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : 0;
    private static double Rotation(JsonElement video)
    {
        if (!video.TryGetProperty("side_data_list", out var list)) { return 0; }
        if (list.ValueKind != JsonValueKind.Array) { throw new InvalidDataException("Video rotation metadata is invalid."); }
        foreach (var value in list.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("rotation", out var field))
            {
                if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var rotation) || !double.IsFinite(rotation))
                { throw new InvalidDataException("Video rotation metadata is invalid."); }
                return rotation;
            }
        }
        return 0;
    }
    internal sealed record VideoProbe(double Duration, string VideoCodec, string PixelFormat, int Width, int Height, double Rotation,
        string AudioCodec, string SubtitleCodec, int VideoCount, int AudioCount, int SubtitleCount, int TotalStreams);
}
