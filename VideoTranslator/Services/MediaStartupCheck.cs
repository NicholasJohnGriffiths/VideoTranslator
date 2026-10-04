using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class MediaStartupCheck(
    IMediaProcessRunner runner, IOptions<MediaOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await runner.RunAsync(options.Value.FFprobePath, ["-version"], cancellationToken);
        await runner.RunAsync(options.Value.FFmpegPath, ["-version"], cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
