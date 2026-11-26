using System.Text.Json;

namespace Pda.Security;

public static class ManagedRequirements
{
    public static bool Inspect(string startupJson)
    {
        using var data = JsonDocument.Parse(startupJson,
            new JsonDocumentOptions { AllowDuplicateProperties = false });
        var req = data.RootElement.GetProperty("configRequirements")
            .GetProperty("requirements");
        if (req.ValueKind == JsonValueKind.Null) return false;
        if (req.ValueKind != JsonValueKind.Object
            || Excludes(req, "allowedWindowsSandboxImplementations", "mxc")
            || Excludes(req, "allowedApprovalPolicies", "never")
            || Excludes(req, "allowedWebSearchModes", "disabled")
            || Conflicts(req, "defaultPermissions", "pda")
            || Conflicts(req, "cliAuthCredentialsStore", "file"))
            throw new InvalidDataException("Managed runtime policy conflicts.");
        if (req.TryGetProperty("allowedPermissionProfiles", out var profiles)
            && profiles.ValueKind != JsonValueKind.Null
            && (!profiles.TryGetProperty("pda", out var grant)
                || grant.ValueKind != JsonValueKind.True))
            throw new InvalidDataException("Managed profile is unavailable.");
        return true;
    }

    private static bool Excludes(JsonElement obj, string name, string expected)
    {
        if (!obj.TryGetProperty(name, out var prop)
            || prop.ValueKind == JsonValueKind.Null) return false;
        return prop.ValueKind != JsonValueKind.Array
            || !prop.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String
                && p.GetString() == expected);
    }

    private static bool Conflicts(JsonElement obj, string name, string expected)
        => obj.TryGetProperty(name, out var prop)
            && prop.ValueKind != JsonValueKind.Null
            && (prop.ValueKind != JsonValueKind.String
                || prop.GetString() != expected);
}
