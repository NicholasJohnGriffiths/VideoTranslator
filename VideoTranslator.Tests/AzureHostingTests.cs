using System.Text;
using System.Text.Json;
using VideoTranslator.Configuration;
using VideoTranslator.Services;

namespace VideoTranslator.Tests;

public sealed class AzureHostingTests
{
    private const string Tenant = "dc74cf4c-f60c-409a-b4de-69db311bfb6f";
    private const string User = "987cbe21-d0a0-4eb7-a91a-e670f6eb4438";
    private static string Header(string tenant, string user, string type = "aad") =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            auth_typ = type, claims = new[] { new { typ = "tid", val = tenant }, new { typ = "oid", val = user } }
        })));
    private static AzureHostingOptions Hosting(bool enabled = true) => new()
    { Enabled = enabled, TenantId = Tenant, AllowedUserObjectId = User };

    [Fact]
    public void OnlyApprovedTenantAndUserAreAccepted()
    {
        Assert.True(AppServiceAccess.IsAllowed(Header(Tenant, User), Tenant, User));
        Assert.False(AppServiceAccess.IsAllowed(Header(Guid.NewGuid().ToString(), User), Tenant, User));
        Assert.False(AppServiceAccess.IsAllowed(Header(Tenant, Guid.NewGuid().ToString()), Tenant, User));
        Assert.False(AppServiceAccess.IsAllowed(Header(Tenant, User, "facebook"), Tenant, User));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("W10=")]
    [InlineData("e30=")]
    public void MissingOrMalformedPlatformPrincipalIsDenied(string? header) =>
        Assert.False(AppServiceAccess.IsAllowed(header, Tenant, User));

    [Theory]
    [InlineData(null, "true", "AzureBlob", "ManagedIdentity")]
    [InlineData("app", null, "AzureBlob", "ManagedIdentity")]
    [InlineData("app", "false", "AzureBlob", "ManagedIdentity")]
    [InlineData("app", "true", "Local", "ManagedIdentity")]
    [InlineData("app", "true", "AzureBlob", "AzureCli")]
    public void UnsafeHostingConfigurationFailsClosed(string? site, string? auth, string storage, string credential)
    {
        Assert.Throws<InvalidOperationException>(() => Hosting().Validate(site, auth,
            new() { Provider = storage }, new() { CredentialMode = credential }, new(), new()));
    }

    [Fact]
    public void ProductionRequiresOptInAndManagedIdentitiesForActiveServices()
    {
        Assert.Throws<InvalidOperationException>(() => Hosting(false).Validate("app", "true",
            new() { Provider = "AzureBlob" }, new(), new(), new()));
        Hosting().Validate("app", "true", new() { Provider = "AzureBlob" }, new(), new(), new());
        Assert.Throws<InvalidOperationException>(() => Hosting().Validate("app", "true",
            new() { Provider = "AzureBlob" }, new(), new() { Enabled = true, CredentialMode = "AzureCli" }, new()));
        Assert.Throws<InvalidOperationException>(() => Hosting().Validate("app", "true",
            new() { Provider = "AzureBlob" }, new(), new(), new() { Enabled = true, CredentialMode = "AzureCli" }));
    }

    [Fact]
    public void SingleAccountModeDoesNotRequirePlatformEntraAuthentication()
    {
        new AzureHostingOptions { Enabled = true, AuthenticationMode = "SingleAccount" }
            .Validate("app", "false", new() { Provider = "AzureBlob" }, new(), new(), new());
        Assert.Throws<InvalidOperationException>(() =>
            new AzureHostingOptions { Enabled = true, AuthenticationMode = "Unknown" }
                .Validate("app", "true", new() { Provider = "AzureBlob" }, new(), new(), new()));
    }
}
