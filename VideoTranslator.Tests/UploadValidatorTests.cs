using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class UploadValidatorTests
{
    private readonly UploadValidator validator = new(Options.Create(new UploadOptions()));

    [Theory]
    [InlineData(@"C:\fakepath\sample.mp4", "sample.mp4")]
    [InlineData("../../sample.mp4", "sample.mp4")]
    [InlineData("sample.MP4", "sample.MP4")]
    public void FilenameIsOnlyUsedAsSanitizedDisplayMetadata(string original, string expected)
    {
        Assert.Equal(expected, validator.ValidateMetadata(original, "video/mp4", 16));
    }

    [Theory]
    [InlineData("sample.exe", "video/mp4", 16)]
    [InlineData("sample.mp4", "application/octet-stream", 16)]
    [InlineData("sample.mp4", "video/mp4", 0)]
    [InlineData("sample.mp4", "video/mp4", 104857601)]
    [InlineData("", "video/mp4", 16)]
    [InlineData("sample\n.mp4", "video/mp4", 16)]
    public void InvalidMetadataIsRejected(string name, string mime, long length)
    {
        Assert.Throws<UploadValidationException>(() => validator.ValidateMetadata(name, mime, length));
    }

    [Fact]
    public void ExactSizeLimitIsAccepted()
    {
        Assert.Equal("sample.mp4", validator.ValidateMetadata("sample.mp4", "video/mp4", 104857600));
    }

    [Fact]
    public async Task ValidHeaderIsAccepted()
    {
        await using var stream = new MemoryStream(Mp4Header());
        await validator.ValidateContentAsync(stream, CancellationToken.None);
    }

    [Fact]
    public async Task RenamedNonMp4IsRejected()
    {
        await using var stream = new MemoryStream(new byte[16]);
        await Assert.ThrowsAsync<UploadValidationException>(() =>
            validator.ValidateContentAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedHeaderIsRejected()
    {
        await using var stream = new MemoryStream(new byte[4]);
        await Assert.ThrowsAsync<UploadValidationException>(() =>
            validator.ValidateContentAsync(stream, CancellationToken.None));
    }

    // A container-header fixture only, not a playable video.
    internal static byte[] Mp4Header() => [0, 0, 0, 16, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0];
}
