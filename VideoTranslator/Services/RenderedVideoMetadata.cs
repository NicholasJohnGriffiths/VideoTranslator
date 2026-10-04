using System.Security.Cryptography;
using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public static class RenderedVideoMetadata
{
    public static string Name(RenderArtifact artifact) => artifact switch
    {
        RenderArtifact.Video => "translated-video.mp4",
        RenderArtifact.Subtitles => "subtitles.vtt",
        RenderArtifact.Script => "approved-script.json",
        _ => throw new ArgumentOutOfRangeException(nameof(artifact))
    };
    public static long Limit(RenderArtifact artifact) => artifact == RenderArtifact.Video ? 600_000_000 : 16_000_000;

    public static void Validate(RenderedVideoResult result, string requestId)
    {
        if (!Guid.TryParseExact(requestId, "N", out _)
            || result.RequestId != requestId || !double.IsFinite(result.DurationSeconds)
            || result.DurationSeconds <= 0 || result.DurationSeconds >= 18000 || result.Files is null
            || result.Files.Count != 3 || Enum.GetValues<RenderArtifact>().Any(artifact =>
                !result.Files.TryGetValue(artifact, out var file) || file is null
                || file.Length <= 0 || file.Length > Limit(artifact)
                || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)))
        {
            throw new InvalidDataException("Rendered output metadata is invalid.");
        }
    }

    public static async Task<RenderedFile> InspectAsync(string path, RenderArtifact artifact, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > Limit(artifact))
        {
            throw new InvalidDataException("Rendered artifact is empty or oversized.");
        }
        return new(stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, token)));
    }

    public static async Task VerifyAsync(Stream stream, RenderedFile file, CancellationToken token)
    {
        stream.Position = 0;
        if (stream.Length != file.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != file.Sha256)
        {
            throw new InvalidDataException("Rendered artifact differs from its committed metadata.");
        }
        stream.Position = 0;
    }

    public static async Task CopyAsync(Stream source, Stream destination, RenderArtifact artifact, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > Limit(artifact)) { throw new InvalidDataException("Rendered artifact exceeds its size limit."); }
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (total == 0) { throw new InvalidDataException("Rendered artifact is empty."); }
    }

    internal static async Task<RenderedVideoResult> ReadAsync(Stream stream, string requestId, CancellationToken token)
    {
        var result = await JsonSerializer.DeserializeAsync<RenderedVideoResult>(stream, JobMetadata.JsonOptions, token)
            ?? throw new InvalidDataException("Rendered output metadata is empty.");
        Validate(result, requestId);
        return result;
    }
}
