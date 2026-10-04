using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace VideoTranslator.Configuration;

public sealed class BlobStorageOptions
{
    public const string SectionName = "BlobStorage";
    public string ServiceUri { get; init; } = string.Empty;
    public string ContainerName { get; init; } = string.Empty;
    public string CredentialMode { get; init; } = "ManagedIdentity";
    public string ManagedIdentityClientId { get; init; } = string.Empty;
}

public sealed class BlobStorageOptionsValidator : IValidateOptions<BlobStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, BlobStorageOptions options)
    {
        if (!Uri.TryCreate(options.ServiceUri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/"
            || uri.Port != 443 || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return ValidateOptionsResult.Fail("BlobStorage:ServiceUri must be an HTTPS service root without credentials.");
        }

        if (!Regex.IsMatch(options.ContainerName, "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$",
                RegexOptions.CultureInvariant)
            || options.ContainerName.Contains("--", StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail("BlobStorage:ContainerName must be a valid Azure container name.");
        }

        if (options.CredentialMode is not ("AzureCli" or "ManagedIdentity"))
        {
            return ValidateOptionsResult.Fail("BlobStorage:CredentialMode must be AzureCli or ManagedIdentity.");
        }

        if (!string.IsNullOrEmpty(options.ManagedIdentityClientId)
            && !Guid.TryParse(options.ManagedIdentityClientId, out _))
        {
            return ValidateOptionsResult.Fail("BlobStorage:ManagedIdentityClientId must be a client ID GUID.");
        }

        return ValidateOptionsResult.Success;
    }
}
