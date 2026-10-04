using VideoTranslator.Configuration;

namespace VideoTranslator.Tests;

public sealed class StorageConfigurationTests
{
    [Theory]
    [InlineData("Local", true)]
    [InlineData("AzureBlob", true)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void ProviderMustBeExplicit(string provider, bool valid)
    {
        Assert.Equal(valid, new JobStorageOptionsValidator()
            .Validate(null, new JobStorageOptions { Provider = provider }).Succeeded);
    }

    [Theory]
    [InlineData("https://account.blob.core.windows.net/", "video-jobs", "AzureCli", true)]
    [InlineData("https://account.blob.core.windows.net/", "video-jobs", "ManagedIdentity", true)]
    [InlineData("http://account.blob.core.windows.net/", "video-jobs", "AzureCli", false)]
    [InlineData("https://account.blob.core.windows.net/?sig=secret", "video-jobs", "AzureCli", false)]
    [InlineData("https://account.blob.core.windows.net/container", "video-jobs", "AzureCli", false)]
    [InlineData("https://account.blob.core.windows.net/", "VideoJobs", "AzureCli", false)]
    [InlineData("https://account.blob.core.windows.net/", "video--jobs", "AzureCli", false)]
    [InlineData("https://account.blob.core.windows.net/", "ab", "AzureCli", false)]
    [InlineData("https://account.blob.core.windows.net/", "video-jobs", "Default", false)]
    public void BlobOptionsValidateEndpointsContainersAndCredentialModes(
        string uri, string container, string mode, bool valid)
    {
        Assert.Equal(valid, new BlobStorageOptionsValidator().Validate(null, new BlobStorageOptions
        {
            ServiceUri = uri, ContainerName = container, CredentialMode = mode
        }).Succeeded);
    }
}
