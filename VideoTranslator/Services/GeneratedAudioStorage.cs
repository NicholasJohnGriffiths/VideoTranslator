using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

internal static class GeneratedAudioStorage
{
    public static void ValidateIds(string jobId, string requestId)
    {
        JobMetadata.ValidateJobId(jobId);
        JobMetadata.ValidateJobId(requestId);
    }

    public static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += count;
            if (total > 600_000_000)
            {
                throw new InvalidDataException("Generated audio exceeds the supported size.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        if (total <= 44)
        {
            throw new InvalidDataException("Generated audio is empty.");
        }
    }

    public static async Task<TimedAudioResult> ReadAsync(Stream input, string requestId, CancellationToken cancellationToken)
    {
        var result = await JsonSerializer.DeserializeAsync<TimedAudioResult>(input, JobMetadata.JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Generated audio metadata is empty.");
        if (result.RequestId != requestId)
        {
            throw new InvalidDataException("Generated audio metadata has an incorrect request identifier.");
        }
        return result;
    }
}
