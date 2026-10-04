using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class FFmpegAudioService(
    IMediaProcessRunner runner, IOptions<MediaOptions> options,
    ILogger<FFmpegAudioService> logger) : IAudioService
{
    public async Task<string> ExtractAudioAsync(
        string videoPath, string workingDirectory, CancellationToken cancellationToken)
    {
        var probe = await runner.RunAsync(options.Value.FFprobePath,
        [
            "-v", "error", "-protocol_whitelist", "file,pipe",
            "-show_entries", "format=format_name,duration:stream=codec_type",
            "-of", "json", videoPath
        ], cancellationToken);
        ValidateProbe(probe);

        var audioPath = Path.Combine(workingDirectory, "original-audio.wav");
        await runner.RunAsync(options.Value.FFmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-xerror",
            "-protocol_whitelist", "file,pipe", "-threads", "1", "-i", videoPath,
            "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
            "-threads", "1", audioPath
        ], cancellationToken);

        if (!File.Exists(audioPath) || new FileInfo(audioPath).Length <= 44)
        {
            throw new MediaProcessingException("No audio samples could be extracted from the video.");
        }
        var audioProbe = await runner.RunAsync(options.Value.FFprobePath,
        [
            "-v", "error", "-protocol_whitelist", "file,pipe",
            "-show_entries", "format=duration:stream=codec_name,sample_rate,channels,bits_per_sample",
            "-of", "json", audioPath
        ], cancellationToken);
        ValidateAudioProbe(audioProbe);
        return audioPath;
    }

    private void ValidateAudioProbe(string probe)
    {
        try
        {
            using var document = JsonDocument.Parse(probe);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object
                || !format.TryGetProperty("duration", out var duration) || duration.ValueKind != JsonValueKind.String
                || !double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds <= 0
                || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array
                || streams.GetArrayLength() != 1)
            {
                throw new MediaProcessingException("The extracted audio is empty or invalid.");
            }
            var stream = streams[0];
            if (stream.ValueKind != JsonValueKind.Object
                || !stream.TryGetProperty("codec_name", out var codec) || codec.ValueKind != JsonValueKind.String
                || codec.GetString() != "pcm_s16le"
                || !stream.TryGetProperty("sample_rate", out var rate) || rate.ValueKind != JsonValueKind.String
                || rate.GetString() != "16000"
                || !stream.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Number
                || !channels.TryGetInt32(out var channelCount)
                || channelCount != 1
                || !stream.TryGetProperty("bits_per_sample", out var bits) || bits.ValueKind != JsonValueKind.Number
                || !bits.TryGetInt32(out var bitCount)
                || bitCount != 16)
            {
                throw new MediaProcessingException("The extracted audio does not have the required speech format.");
            }
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "FFprobe returned invalid extracted-audio metadata.");
            throw new MediaProcessingException("Extracted audio metadata could not be read.", exception);
        }
    }

    private void ValidateProbe(string probe)
    {
        try
        {
            using var document = JsonDocument.Parse(probe);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object
                || !format.TryGetProperty("format_name", out var name)
                || name.ValueKind != JsonValueKind.String
                || !name.GetString()!.Split(',').Contains("mp4", StringComparer.Ordinal)
                || !format.TryGetProperty("duration", out var duration)
                || duration.ValueKind != JsonValueKind.String
                || !double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds <= 0
                || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                throw new MediaProcessingException("The video must be a valid MP4 with a positive duration.");
            }
            var types = streams.EnumerateArray()
                .Where(stream => stream.ValueKind == JsonValueKind.Object
                    && stream.TryGetProperty("codec_type", out var type) && type.ValueKind == JsonValueKind.String)
                .Select(stream => stream.GetProperty("codec_type").GetString()).ToArray();
            if (!types.Contains("video") || !types.Contains("audio"))
            {
                throw new MediaProcessingException("The MP4 must contain both a video track and an audio track.");
            }
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "FFprobe returned invalid media metadata.");
            throw new MediaProcessingException("Video metadata could not be read.", exception);
        }
    }
}
