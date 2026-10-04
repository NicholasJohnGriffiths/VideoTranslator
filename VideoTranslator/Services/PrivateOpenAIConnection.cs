using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class PrivateOpenAIConnection(IOptions<AzureOpenAIOptions> options)
{
    public void ValidateAddresses(string host, int port, IReadOnlyList<IPAddress> resolved)
    {
        var endpoint = new Uri(options.Value.Endpoint);
        var allowed = options.Value.PrivateEndpointAddresses.Select(IPAddress.Parse).ToHashSet();
        if (!string.Equals(host, endpoint.Host, StringComparison.OrdinalIgnoreCase) || port != 443
            || resolved.Count == 0 || resolved.Any(address => !allowed.Contains(address)
                || !AzureOpenAIOptionsValidator.IsPrivate(address)))
        {
            throw new TranslationException(
                "OpenAI private routing is unavailable. Connect to the Azure VNet and configure private DNS; public fallback is prohibited.");
        }
    }

    public SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var target = context.DnsEndPoint;
            var addresses = await Dns.GetHostAddressesAsync(target.Host, cancellationToken);
            ValidateAddresses(target.Host, target.Port, addresses);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Pin the socket to a verified private address; TLS still validates the original hostname.
                await socket.ConnectAsync(new IPEndPoint(addresses[0], target.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
