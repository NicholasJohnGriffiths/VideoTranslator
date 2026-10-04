namespace VideoTranslator.Models;

public enum RenderArtifact { Video, Subtitles, Script }

public sealed class RenderedVideoResult
{
    public string RequestId { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
    public Dictionary<RenderArtifact, RenderedFile> Files { get; init; } = [];
}

public sealed record RenderedFile(long Length, string Sha256);
