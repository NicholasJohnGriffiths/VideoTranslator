using VideoTranslator.Models;

namespace VideoTranslator.Services;

public static class SpeechTimingWindow
{
    public static double EndSeconds(SpeechGeneration script, int index, double videoSeconds) =>
        script.UseAvailableGaps
            ? index + 1 < script.Segments.Count ? script.Segments[index + 1].Start.TotalSeconds : videoSeconds
            : script.Segments[index].End.TotalSeconds;

    public static double SlotSeconds(SpeechGeneration script, int index, double videoSeconds) =>
        (Samples(EndSeconds(script, index, videoSeconds)) - Samples(script.Segments[index].Start.TotalSeconds)) / 16000d;

    private static long Samples(double seconds) =>
        checked((long)Math.Round(seconds * 16000, MidpointRounding.AwayFromZero));
}
