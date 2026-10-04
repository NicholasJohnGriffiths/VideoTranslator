using System.Net;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class PrivateOpenAIStartupCheck(
    PrivateOpenAIConnection connection, IOptions<AzureOpenAIOptions> options,
    ILogger<PrivateOpenAIStartupCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var endpoint = new Uri(options.Value.Endpoint);
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        connection.ValidateAddresses(endpoint.Host, endpoint.Port, addresses);
        logger.LogInformation("OpenAI DNS resolves only to approved private endpoint addresses. Actual routing and RBAC still require verification.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
