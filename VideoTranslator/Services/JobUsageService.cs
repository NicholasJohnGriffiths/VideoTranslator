using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class JobUsageService(IJobUsageStorage storage, IOptions<JobCostOptions> options)
{
    public async Task<JobUsage> StartAsync(string jobId, string service, CancellationToken token)
    {
        var usage = new JobUsage { Service = service };
        await storage.AppendAsync(jobId, usage, token);
        return usage;
    }

    public async Task CompleteAsync(string jobId, JobUsage request, double seconds, long characters,
        long input, long cached, long output, CancellationToken token)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || characters < 0 || input < 0 || cached < 0 || cached > input || output < 0)
            throw new InvalidDataException("Invalid AI usage measurements.");
        var rates = options.Value;
        var cost = request.Service switch
        {
            "Transcription" => (decimal)seconds / 3600 * rates.TranscriptionPerHour,
            "Synthesis" => characters / 1_000_000m * rates.SynthesisPerMillionCharacters,
            "OpenAI" => (input - cached) / 1000m * rates.InputPerThousandTokens
                + cached / 1000m * rates.CachedInputPerThousandTokens + output / 1000m * rates.OutputPerThousandTokens,
            _ => throw new InvalidDataException("Unknown AI usage service.")
        };
        await storage.AppendAsync(jobId, request with
        {
            Completed = true, RecordedUtc = DateTime.UtcNow, Seconds = seconds, Characters = characters,
            InputTokens = input, CachedInputTokens = cached, OutputTokens = output,
            EstimatedNzd = cost, RateDate = rates.RateDate
        }, token);
    }

    public async Task<JobCostSummary> GetAsync(string jobId, CancellationToken token)
    {
        var records = await storage.ReadAsync(jobId, token);
        if (records.Any(record => !Guid.TryParseExact(record.RequestId, "N", out _)
            || record.Service is not ("Transcription" or "Synthesis" or "OpenAI")
            || (record.Completed && (record.EstimatedNzd is null || record.EstimatedNzd < 0
                || string.IsNullOrWhiteSpace(record.RateDate)))))
            throw new JobStorageException("Stored usage records are invalid.", new InvalidDataException("Invalid cost metadata."));
        var completed = records.Where(record => record.Completed).ToArray();
        var resolved = completed.Select(record => record.RequestId).ToHashSet();
        return new(completed.Sum(record => record.EstimatedNzd!.Value),
            records.Count(record => !record.Completed && !resolved.Contains(record.RequestId)), completed);
    }
}
