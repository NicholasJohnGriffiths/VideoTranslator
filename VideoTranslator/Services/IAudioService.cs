namespace VideoTranslator.Services;

public interface IAudioService
{
    Task<string> ExtractAudioAsync(string videoPath, string workingDirectory, CancellationToken cancellationToken);
}
