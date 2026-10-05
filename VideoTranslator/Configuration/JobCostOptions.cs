namespace VideoTranslator.Configuration;

public sealed class JobCostOptions
{
    public decimal TranscriptionPerHour { get; init; } = 0.6360m;
    public decimal SynthesisPerMillionCharacters { get; init; } = 26.4994m;
    public decimal InputPerThousandTokens { get; init; } = 0.0049m;
    public decimal CachedInputPerThousandTokens { get; init; } = 0.0024m;
    public decimal OutputPerThousandTokens { get; init; } = 0.0194m;
    public string RateDate { get; init; } = "2026-10-05";
}
