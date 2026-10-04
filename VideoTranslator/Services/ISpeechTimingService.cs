using VideoTranslator.Models;

namespace VideoTranslator.Services;

public interface ISpeechTimingService
{
    Task<IReadOnlyList<SegmentAudioTiming>> SynchronizeAsync(
        SpeechGeneration script, IReadOnlyList<string?> audioPaths, double videoDurationSeconds,
        string outputPath, CancellationToken cancellationToken);
}
