using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace VideoTranslator.Services;

public sealed class BlobStorageStartupCheck(
    BlobContainerClient container, ILogger<BlobStorageStartupCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var properties = await container.GetPropertiesAsync(cancellationToken: cancellationToken);
        if (properties.Value.PublicAccess != PublicAccessType.None)
        {
            throw new InvalidOperationException(
                "Video job container permits anonymous access. Set container public access to None before starting.");
        }

        logger.LogInformation("Private Blob job container access verified.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
