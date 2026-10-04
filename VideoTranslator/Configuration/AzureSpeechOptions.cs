using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace VideoTranslator.Configuration;

public sealed class AzureSpeechOptions
{
    public const string SectionName = "AzureSpeech";
    public bool Enabled { get; init; }
    public bool SynthesisEnabled { get; init; }
    public string ResourceId { get; init; } = string.Empty;
    [Range(1, 600)]
    public int SynthesisTimeoutSeconds { get; init; } = 120;
    public string Region { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string CredentialMode { get; init; } = "ManagedIdentity";
    public string ManagedIdentityClientId { get; init; } = string.Empty;
    public string SourceLocale { get; init; } = "en-NZ";
    public string ApiVersion { get; init; } = "2025-10-15";
    [Range(1, 3600)]
    public int TimeoutSeconds { get; init; } = 600;
}

public sealed class AzureSpeechOptionsValidator : IValidateOptions<AzureSpeechOptions>
{
    public ValidateOptionsResult Validate(string? name, AzureSpeechOptions options)
    {
        if (!options.Enabled && !options.SynthesisEnabled)
        {
            return ValidateOptionsResult.Success;
        }
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/" || uri.Port != 443
            || !uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return ValidateOptionsResult.Fail("AzureSpeech:Endpoint must be an HTTPS custom-domain Speech resource root.");
        }
        if (options.CredentialMode is not ("AzureCli" or "ManagedIdentity")
            || (!string.IsNullOrEmpty(options.ManagedIdentityClientId)
                && !Guid.TryParse(options.ManagedIdentityClientId, out _)))
        {
            return ValidateOptionsResult.Fail("Configure a valid Speech credential mode and optional identity client ID.");
        }
        if (options.Enabled && (!DateOnly.TryParseExact(options.ApiVersion, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !options.SourceLocale.StartsWith("en-", StringComparison.Ordinal)
            || !CultureInfo.GetCultures(CultureTypes.SpecificCultures).Any(culture => culture.Name == options.SourceLocale)))
        {
            return ValidateOptionsResult.Fail("Configure an English source locale and a dated Speech API version.");
        }
        if (options.SynthesisEnabled && !Regex.IsMatch(options.ResourceId,
            @"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[a-zA-Z0-9_.()-]+/providers/Microsoft\.CognitiveServices/accounts/[a-zA-Z0-9-]+$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            return ValidateOptionsResult.Fail("Speech synthesis requires the full Speech resource ID for keyless authorization.");
        }
        return ValidateOptionsResult.Success;
    }
}
