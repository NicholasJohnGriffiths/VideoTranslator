using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class MediaAudioInspector(IMediaProcessRunner runner, IOptions<MediaOptions> options)
{
    public async Task<double> GetVideoDurationAsync(string path, CancellationToken cancellationToken)
    {
        var json = await runner.RunAsync(options.Value.FFprobePath,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "format=duration", "-of", "json", path],
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        var seconds = Duration(document.RootElement);
        if (seconds >= 5 * 60 * 60)
        {
            throw new SpeechTimingException("Full audio generation supports videos shorter than five hours.");
        }
        return seconds;
    }

    public async Task<double> GetPcmDurationAsync(string path, CancellationToken cancellationToken)
    {
        var json = await runner.RunAsync(options.Value.FFprobePath,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
                "format=duration:stream=codec_name,sample_rate,channels,bits_per_sample", "-of", "json", path],
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var seconds = Duration(root);
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array
            || streams.GetArrayLength() != 1 || streams[0].ValueKind != JsonValueKind.Object
            || !StringEquals(streams[0], "codec_name", "pcm_s16le") || !StringEquals(streams[0], "sample_rate", "16000")
            || !IntegerEquals(streams[0], "channels", 1) || !IntegerEquals(streams[0], "bits_per_sample", 16))
        {
            throw new InvalidDataException("Generated audio is not mono 16 kHz 16-bit PCM.");
        }
        return seconds;
    }

    private static double Duration(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.Object
            || !format.TryGetProperty("duration", out var duration)
            || duration.ValueKind != JsonValueKind.String
            || !double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds <= 0)
        {
            throw new InvalidDataException("Media duration is missing or invalid.");
        }
        return seconds;
    }

    private static bool StringEquals(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == expected;
    private static bool IntegerEquals(JsonElement value, string name, int expected) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number
        && field.TryGetInt32(out var number) && number == expected;
}
