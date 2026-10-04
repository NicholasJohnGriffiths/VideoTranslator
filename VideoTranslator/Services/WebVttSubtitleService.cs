using System.Globalization;
using System.Text;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public sealed class WebVttSubtitleService : ISubtitleService
{
    public async Task<string> GenerateWebVttAsync(
        IReadOnlyList<VideoSegment> segments, string outputPath, CancellationToken cancellationToken)
    {
        var text = new StringBuilder("WEBVTT\n\n");
        foreach (var segment in segments)
        {
            var effective = segment.EffectiveText;
            if (string.IsNullOrWhiteSpace(effective)) { continue; }
            if (segment.Start < TimeSpan.Zero || segment.End <= segment.Start
                || Timestamp(segment.Start) == Timestamp(segment.End)
                || effective.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            {
                throw new InvalidDataException("Subtitle text or timestamps are invalid.");
            }
            var lines = effective.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var escaped = string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)))
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            text.Append(CultureInfo.InvariantCulture, $"{segment.Sequence}\n{Timestamp(segment.Start)} --> {Timestamp(segment.End)}\n{escaped}\n\n");
        }
        await File.WriteAllTextAsync(outputPath, text.ToString(), new UTF8Encoding(false, true), cancellationToken);
        return outputPath;
    }

    private static string Timestamp(TimeSpan time)
    {
        var milliseconds = checked((long)Math.Round(time.TotalMilliseconds, MidpointRounding.AwayFromZero));
        return string.Create(CultureInfo.InvariantCulture,
            $"{milliseconds / 3600000:D2}:{milliseconds / 60000 % 60:D2}:{milliseconds / 1000 % 60:D2}.{milliseconds % 1000:D3}");
    }
}
