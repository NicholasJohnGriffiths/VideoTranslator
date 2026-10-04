using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

internal static class TranscriptMetadata
{
    public static void Validate(Transcript transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript.SourceLanguage) || transcript.Segments is null
            || transcript.Segments.Count == 0)
        {
            throw new InvalidDataException("Stored transcript is empty or missing its source language.");
        }
        TimeSpan previous = TimeSpan.Zero;
        for (var index = 0; index < transcript.Segments.Count; index++)
        {
            var segment = transcript.Segments[index];
            if (segment is null || segment.Sequence != index + 1 || segment.Start < previous
                || segment.Start < TimeSpan.Zero || segment.End <= segment.Start
                || string.IsNullOrWhiteSpace(segment.OriginalText))
            {
                throw new InvalidDataException("Stored transcript has invalid segments or timestamps.");
            }
            previous = segment.Start;
        }
    }

    public static async Task<Transcript> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var transcript = await JsonSerializer.DeserializeAsync<Transcript>(
            input, JobMetadata.JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Stored transcript is empty.");
        Validate(transcript);
        return transcript;
    }

    public static async Task CopyAudioAsync(Stream input, string destinationPath, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destinationPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += count;
            if (total >= 500_000_000)
            {
                throw new TranscriptionException("Extracted audio exceeds the 500 MB transcription limit. Try a shorter video.");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}
