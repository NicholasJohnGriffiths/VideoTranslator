using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class UploadValidator(IOptions<UploadOptions> options) : IUploadValidator
{
    public string ValidateMetadata(string fileName, string contentType, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255
            || fileName.Any(char.IsControl))
        {
            throw new UploadValidationException("Choose a video with a valid filename of at most 255 characters.");
        }

        var displayName = fileName.Replace('\\', '/').Split('/').Last();
        if (!string.Equals(Path.GetExtension(displayName), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadValidationException("Only MP4 videos are supported.");
        }

        if (!string.Equals(contentType, "video/mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadValidationException("The uploaded file must have the video/mp4 content type.");
        }

        if (length < 12 || length > options.Value.MaxFileSizeBytes)
        {
            throw new UploadValidationException(
                $"Choose a non-empty MP4 video no larger than {options.Value.MaxFileSizeBytes / 1024 / 1024} MB.");
        }

        return displayName;
    }

    public async Task ValidateContentAsync(Stream content, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        try
        {
            await content.ReadExactlyAsync(header, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new UploadValidationException("The file is too short to be an MP4 video.");
        }

        // This checks the container signature, not codecs or whether a playable video track exists.
        var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        if (Encoding.ASCII.GetString(header, 4, 4) != "ftyp" || boxSize < 16
            || boxSize > content.Length)
        {
            throw new UploadValidationException("The uploaded file does not have a valid MP4 container header.");
        }
    }
}
