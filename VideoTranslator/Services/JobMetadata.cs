using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

internal static class JobMetadata
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static void ValidateJobId(string jobId)
    {
        if (!Guid.TryParseExact(jobId, "N", out _))
        {
            throw new ArgumentException("Invalid job identifier.", nameof(jobId));
        }
    }

    public static async Task<VideoJob> ReadAsync(
        Stream metadata, string jobId, CancellationToken cancellationToken)
    {
        var job = await JsonSerializer.DeserializeAsync<VideoJob>(metadata, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Stored job metadata is empty.");
        if (!string.Equals(job.JobId, jobId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored job identifier does not match the requested job.");
        }

        return job;
    }
}
