using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using VideoTranslator.Configuration;

namespace VideoTranslator.Services;

public sealed class SingleAccountCredentials
{
    public const string LoginRateLimitPolicy = "single-account-login";
    private const string VersionClaim = "credential-version";
    private readonly byte[] salt;
    private readonly byte[] hash;
    private readonly string version;
    public string Username { get; }

    public SingleAccountCredentials(IOptions<SingleAccountLoginOptions> options)
    {
        (salt, hash) = options.Value.ReadPasswordHash();
        Username = options.Value.Username;
        version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.PasswordHash)));
    }

    public bool Verify(string username, string password)
    {
        if (username.Length > 64 || password.Length is 0 or > 256)
        {
            return false;
        }
        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt,
            SingleAccountLoginOptions.Iterations, HashAlgorithmName.SHA256, hash.Length);
        try
        {
            var passwordMatches = CryptographicOperations.FixedTimeEquals(candidate, hash);
            return passwordMatches && string.Equals(username, Username, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public ClaimsPrincipal CreatePrincipal() => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Username),
            new Claim(ClaimTypes.Name, Username),
            new Claim(VersionClaim, version)
        ], CookieAuthenticationDefaults.AuthenticationScheme));

    public bool IsCurrentPrincipal(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true
        && principal.FindFirstValue(ClaimTypes.NameIdentifier) == Username
        && principal.FindFirstValue(VersionClaim) == version;
}
