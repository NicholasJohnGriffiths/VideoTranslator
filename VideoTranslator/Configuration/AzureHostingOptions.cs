namespace VideoTranslator.Configuration;

public sealed class AzureHostingOptions
{
    public const string SectionName = "AzureHosting";
    public bool Enabled { get; init; }
    public bool ProcessingEnabled { get; init; } = true;
    public string AuthenticationMode { get; init; } = "MicrosoftEntra";
    public bool UsesSingleAccountLogin => AuthenticationMode == "SingleAccount";
    public string TenantId { get; init; } = string.Empty;
    public string AllowedUserObjectId { get; init; } = string.Empty;

    public void Validate(string? siteName, string? platformAuthentication, JobStorageOptions storage,
        BlobStorageOptions blob, AzureSpeechOptions speech, AzureOpenAIOptions openai)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(siteName)
            || AuthenticationMode is not ("MicrosoftEntra" or "SingleAccount")
            || !UsesSingleAccountLogin && (
                !string.Equals(platformAuthentication, "true", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(TenantId, out _) || !Guid.TryParse(AllowedUserObjectId, out _))
            || storage.Provider != "AzureBlob" || blob.CredentialMode != "ManagedIdentity"
            || (speech.Enabled || speech.SynthesisEnabled) && speech.CredentialMode != "ManagedIdentity"
            || openai.Enabled && openai.CredentialMode != "ManagedIdentity")
        {
            throw new InvalidOperationException(
                "Azure hosting requires an approved authentication mode, private Blob storage and managed identity credentials. MicrosoftEntra mode also requires authenticated App Service and an explicit tenant/user.");
        }
    }
}
