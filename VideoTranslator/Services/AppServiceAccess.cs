using System.Text.Json;

namespace VideoTranslator.Services;

public static class AppServiceAccess
{
    public static bool IsAllowed(string? header, string tenantId, string objectId)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 32768) { return false; }
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(header));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("auth_typ", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "aad" || !root.TryGetProperty("claims", out var claims)
                || claims.ValueKind != JsonValueKind.Array)
            { return false; }
            var tenants = new List<string>();
            var identities = new List<string>();
            foreach (var claim in claims.EnumerateArray())
            {
                if (claim.ValueKind != JsonValueKind.Object
                    || !claim.TryGetProperty("typ", out var name) || name.ValueKind != JsonValueKind.String
                    || !claim.TryGetProperty("val", out var value) || value.ValueKind != JsonValueKind.String)
                { return false; }
                if (name.GetString() is "tid" or "http://schemas.microsoft.com/identity/claims/tenantid")
                { tenants.Add(value.GetString()!); }
                if (name.GetString() is "oid" or "http://schemas.microsoft.com/identity/claims/objectidentifier")
                { identities.Add(value.GetString()!); }
            }
            return tenants.Count == 1 && identities.Count == 1
                && string.Equals(tenants[0], tenantId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(identities[0], objectId, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }
}
