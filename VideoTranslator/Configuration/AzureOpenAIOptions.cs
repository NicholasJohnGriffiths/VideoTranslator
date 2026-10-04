using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Extensions.Options;

namespace VideoTranslator.Configuration;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string DeploymentName { get; init; } = string.Empty;
    public string CredentialMode { get; init; } = "ManagedIdentity";
    public string ManagedIdentityClientId { get; init; } = string.Empty;
    public string ApiVersion { get; init; } = "2024-10-21";
    public List<string> PrivateEndpointAddresses { get; init; } = [];
    [Range(1, 600)]
    public int TimeoutSeconds { get; init; } = 120;
    [Range(1, 20)]
    public int BatchSegmentCount { get; init; } = 20;
    [Range(1000, 16000)]
    public int BatchCharacterLimit { get; init; } = 16000;
}

public sealed class AzureOpenAIOptionsValidator : IValidateOptions<AzureOpenAIOptions>
{
    public ValidateOptionsResult Validate(string? name, AzureOpenAIOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.AbsolutePath != "/"
            || !uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0
            || string.IsNullOrWhiteSpace(options.DeploymentName)
            || options.DeploymentName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            || options.ApiVersion != "2024-10-21")
        {
            return ValidateOptionsResult.Fail("Configure an HTTPS Azure OpenAI root, valid deployment and API version 2024-10-21.");
        }
        if (options.CredentialMode is not ("AzureCli" or "ManagedIdentity")
            || (options.ManagedIdentityClientId.Length != 0 && !Guid.TryParse(options.ManagedIdentityClientId, out _)))
        {
            return ValidateOptionsResult.Fail("Configure an explicit OpenAI credential mode and optional identity client ID.");
        }
        if (options.PrivateEndpointAddresses.Count == 0
            || options.PrivateEndpointAddresses.Any(value => !IPAddress.TryParse(value, out var address) || !IsPrivate(address)))
        {
            return ValidateOptionsResult.Fail("Configure the exact RFC1918 IPv4 addresses of the approved OpenAI private endpoint.");
        }
        return ValidateOptionsResult.Success;
    }

    public static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168));
    }
}
