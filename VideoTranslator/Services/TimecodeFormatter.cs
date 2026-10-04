using System.Globalization;

namespace VideoTranslator.Services;

public static class TimecodeFormatter
{
    public static string Format(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(time), "Timecodes cannot be negative.");
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }
}
