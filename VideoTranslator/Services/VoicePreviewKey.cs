using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoTranslator.Models;

namespace VideoTranslator.Services;

public static class VoicePreviewKey
{
    public static string Create(string text, LanguageOption language) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = 1, text, language.SpeechLocale, language.VoiceName, format = SpeechWaveAudio.OutputFormat
        })))).ToLowerInvariant();

    public static void Validate(string jobId, int sequence, string key)
    {
        JobMetadata.ValidateJobId(jobId);
        if (sequence <= 0 || key.Length != 64 || key.Any(character => !char.IsAsciiHexDigit(character))
            || key != key.ToLowerInvariant())
        {
            throw new ArgumentException("Invalid voice preview identifier.");
        }
    }
}
