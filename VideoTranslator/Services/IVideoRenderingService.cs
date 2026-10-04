namespace VideoTranslator.Services;

public interface IVideoRenderingService
{
    Task<string> RenderTranslatedVideoAsync(
        string videoPath, string audioPath, string subtitlePath, CancellationToken cancellationToken);
}
