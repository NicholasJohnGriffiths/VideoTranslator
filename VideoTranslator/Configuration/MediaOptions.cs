using System.ComponentModel.DataAnnotations;

namespace VideoTranslator.Configuration;

public sealed class MediaOptions
{
    public const string SectionName = "Media";
    [Required]
    public string FFmpegPath { get; init; } = "ffmpeg";
    [Required]
    public string FFprobePath { get; init; } = "ffprobe";
    [Range(1, 3600)]
    public int ProcessTimeoutSeconds { get; init; } = 600;
    [Range(1, 60)]
    public int PollIntervalSeconds { get; init; } = 5;
}
