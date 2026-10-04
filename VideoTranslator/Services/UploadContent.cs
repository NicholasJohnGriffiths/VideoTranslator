namespace VideoTranslator.Services;

internal static class UploadContent
{
    public static async Task CopyAndValidateAsync(
        Stream video, string videoPath, long declaredLength, long maximumLength,
        IUploadValidator validator, CancellationToken cancellationToken)
    {
        await using (var output = new FileStream(videoPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous))
        {
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await video.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += count;
                if (total > maximumLength || total > declaredLength)
                {
                    throw new UploadValidationException("The uploaded video exceeds its permitted size.");
                }
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }

            if (total != declaredLength)
            {
                throw new UploadValidationException("The video upload was incomplete. Please try again.");
            }
        }

        await using var input = File.OpenRead(videoPath);
        await validator.ValidateContentAsync(input, cancellationToken);
    }
}
