namespace VideoTranslator.Models;

public sealed record JobUsage
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string Service { get; init; } = "";
    public DateTime RecordedUtc { get; init; } = DateTime.UtcNow;
    public bool Completed { get; init; }
    public double Seconds { get; init; }
    public long Characters { get; init; }
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal? EstimatedNzd { get; init; }
    public string RateDate { get; init; } = "";
}

public sealed record JobCostSummary(decimal KnownNzd, int UnresolvedRequests,
    IReadOnlyList<JobUsage> CompletedRequests);
