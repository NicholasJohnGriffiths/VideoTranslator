namespace VideoTranslator.Configuration;

public sealed class SingleAccountLoginOptions
{
    public const string SectionName = "SingleAccountLogin";
    public const int Iterations = 600_000;
    public string Username { get; init; } = string.Empty;
    public string PasswordHash { get; init; } = string.Empty;
    public string KeyDirectory { get; init; } = string.Empty;

    public (byte[] Salt, byte[] Hash) ReadPasswordHash()
    {
        if (string.IsNullOrWhiteSpace(Username) || Username.Length > 64
            || Username != Username.Trim() || Username.Any(char.IsControl))
        {
            throw new InvalidOperationException("Configure a single-account username of at most 64 characters.");
        }
        var parts = PasswordHash.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || parts[1] != "600000")
        {
            throw new InvalidOperationException("Configure a PBKDF2-SHA256 password hash using 600,000 iterations.");
        }
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var hash = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || hash.Length != 32)
            {
                throw new InvalidOperationException("The configured password hash must have a 16-byte salt and 32-byte hash.");
            }
            return (salt, hash);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The configured password hash contains invalid Base64.", exception);
        }
    }

    public string ValidateKeyDirectory(string webRoot)
    {
        if (string.IsNullOrWhiteSpace(KeyDirectory) || !Path.IsPathRooted(KeyDirectory))
        {
            throw new InvalidOperationException("Configure an absolute persistent authentication key directory outside the web root.");
        }
        var directory = Path.GetFullPath(KeyDirectory);
        var relative = Path.GetRelativePath(Path.GetFullPath(webRoot), directory);
        if (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Authentication keys must not be stored inside the public web root.");
        }
        return directory;
    }
}
