using System.Buffers.Binary;

namespace VideoTranslator.Services;

public static class SpeechWaveAudio
{
    public const string OutputFormat = "riff-16khz-16bit-mono-pcm";
    public const int MaxBytes = 20_000_000;

    public static TimeSpan Validate(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.Length < 44 || data.Length > MaxBytes || !data[..4].SequenceEqual("RIFF"u8)
            || !data.Slice(8, 4).SequenceEqual("WAVE"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) != data.Length - 8)
        {
            throw new InvalidDataException("Speech returned invalid or oversized WAV audio.");
        }
        var hasFormat = false;
        var samples = 0;
        var offset = 12;
        while (offset < data.Length)
        {
            if (data.Length - offset < 8)
            {
                throw new InvalidDataException("Speech WAV contains a truncated chunk.");
            }
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            if (length > data.Length - offset - 8)
            {
                throw new InvalidDataException("Speech WAV chunk exceeds the response length.");
            }
            var chunk = data.Slice(offset + 8, (int)length);
            if (data.Slice(offset, 4).SequenceEqual("fmt "u8))
            {
                if (hasFormat || chunk.Length < 16
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 1
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 16000
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]) != 32000
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]) != 2
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16)
                {
                    throw new InvalidDataException("Speech WAV is not mono 16 kHz 16-bit PCM.");
                }
                hasFormat = true;
            }
            else if (data.Slice(offset, 4).SequenceEqual("data"u8))
            {
                if (samples != 0 || length == 0 || length % 2 != 0)
                {
                    throw new InvalidDataException("Speech WAV samples are empty or invalid.");
                }
                samples = (int)length;
            }
            offset += 8 + (int)length + (int)(length % 2);
        }
        if (!hasFormat || samples == 0 || offset != data.Length || samples >= 600 * 32000)
        {
            throw new InvalidDataException("Speech WAV is empty or reaches the service's 10-minute truncation limit.");
        }
        return TimeSpan.FromSeconds(samples / 32000d);
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (output.Length + count > MaxBytes)
            {
                throw new InvalidDataException("Speech audio exceeds the maximum preview size.");
            }
            await output.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
        return output.ToArray();
    }
}
