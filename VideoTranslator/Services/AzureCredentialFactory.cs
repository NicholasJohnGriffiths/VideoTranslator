using Azure.Core;
using Azure.Identity;

namespace VideoTranslator.Services;

public static class AzureCredentialFactory
{
    public static TokenCredential Create(string mode, string clientId) => mode switch
    {
        "AzureCli" => new AzureCliCredential(),
        "ManagedIdentity" => new ManagedIdentityCredential(string.IsNullOrEmpty(clientId)
            ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(clientId)),
        _ => throw new InvalidOperationException("Unknown Azure credential mode.")
    };
}
