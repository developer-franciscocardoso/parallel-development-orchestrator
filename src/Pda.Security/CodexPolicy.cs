using System.Collections.ObjectModel;
using System.Text.Json;

namespace Pda.Security;

public sealed record CodexProfile(string Executable, string Home,
    IReadOnlyList<string>? ToolDirectories = null,
    IReadOnlyList<string>? ReadOnlyDirectories = null,
    Uri? OpenAiEndpoint = null);

public sealed record PolicyContext(string Executable, string BinarySha256,
    string Version, string Home, string Worktree, IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Environment, bool ManagedRequirements);

// The host supplies a trusted deployment verifier when it can independently
// authenticate effective *session* tool restrictions and MXC confinement.
public interface IManagedPolicyVerifier
{
    Task<bool> VerifyAsync(PolicyContext context, CancellationToken token);
}

public static class CodexPolicy
{
    internal static readonly string[] Disabled =
    [
        "apps", "plugins", "remote_plugin", "hooks", "multi_agent",
        "multi_agent_v2", "browser_use", "browser_use_external",
        "computer_use", "in_app_browser", "skill_mcp_dependency_install",
        "enable_mcp_apps", "standalone_web_search", "web_search_request",
        "web_search_cached", "runtime_agent_instances", "code_mode", "code_mode_host",
        "agent_message_board", "agent_message_board_mcp",
        "request_permissions_tool", "skill_search"
    ];

    public static IReadOnlyList<string> Options { get; } = Array.AsReadOnly(
        new[] {
            "windows.sandbox=\"mxc\"", "default_permissions=\"pda\"",
            "permissions.pda.extends=\":workspace\"",
            "permissions.pda.filesystem={\":root\"=\"deny\",\":minimal\"=\"read\"}",
            "permissions.pda.network.enabled=false",
            "web_search=\"disabled\"", "approval_policy=\"never\"",
            "cli_auth_credentials_store=\"file\"",
            "shell_environment_policy.inherit=\"none\""
        }.Concat(Disabled.Select(name => $"features.{name}=false")).ToArray());

    public static IReadOnlyList<string> ConfigArguments() =>
        Options.SelectMany(option => new[] {"-c", option}).ToArray();

    public static IReadOnlyList<string> WorkerOptions(CodexProfile profile,
        string worktree)
    {
        string tree = TrustedPaths.Directory(worktree);
        var env = EnvironmentFor(profile, tree);
        var grants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [":root"] = "deny", [":minimal"] = "read",
                [":tmpdir"] = "deny", [":slash_tmp"] = "deny"
            };
        var dirs = (profile.ToolDirectories ?? [])
            .Concat(profile.ReadOnlyDirectories ?? []);
        foreach (string item in dirs)
        {
            string path = TrustedPaths.Directory(item);
            TrustedPaths.Disjoint(path, tree);
            TrustedPaths.Disjoint(path, profile.Home);
            TrustedPaths.Disjoint(path, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex"));
            TrustedPaths.OutsideRepository(path);
            grants[path] = "read";
        }
        string fs = string.Join(",", grants.Select(p =>
            JsonSerializer.Serialize(p.Key) + "=" + JsonSerializer.Serialize(p.Value)));
        string win = JsonSerializer.Serialize(env["SystemRoot"]);
        string paths = JsonSerializer.Serialize(env["PATH"]);
        string project = JsonSerializer.Serialize(tree);
        var options = Options.Where(s => !s.StartsWith("permissions.pda.filesystem="));
        return options.Concat(ProviderOptions(profile)).Concat([
            $"permissions.pda.filesystem={{{fs}}}",
            $"shell_environment_policy.set={{SYSTEMROOT={win},PATH={paths}}}",
            "projects={" + project + "={trust_level=\"untrusted\"}}"
        ]).SelectMany(s => new[] { "-c", s }).ToArray();
    }

    public static IReadOnlyList<string> ProviderOptions(CodexProfile profile)
    {
        if (profile.OpenAiEndpoint == null) return [];
        var route = profile.OpenAiEndpoint.AbsoluteUri.TrimEnd('/');
        if (route is not ("https://api.openai.com/v1" or
            "https://chatgpt.com/backend-api/codex"))
            throw new InvalidDataException("Unsupported authenticated model endpoint.");
        return [
            "model_provider=\"pda\"",
            "model_providers.pda={name=\"PDA OpenAI\",base_url=" +
            JsonSerializer.Serialize(route) +
            ",wire_api=\"responses\",requires_openai_auth=true}"
        ];
    }
    public static bool DisabledFeaturesObserved(string listing)
    {
        var rows = listing.Split('\n').Select(line =>
            line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3).ToLookup(parts => parts[0]);
        return Disabled.All(name => rows[name].Count() == 1
            && rows[name].Single()[^1] == "false");
    }

    public static bool EmptyCatalogs(string mcpJson, string pluginJson)
    {
        try
        {
            return CheckCatalogs(mcpJson, pluginJson);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException
            or InvalidOperationException) { return false; }
    }

    private static bool CheckCatalogs(string mcpJson, string pluginJson)
    {
        var strict = new JsonDocumentOptions { AllowDuplicateProperties = false };
        using var mcp = JsonDocument.Parse(mcpJson, strict);
        using var plugins = JsonDocument.Parse(pluginJson, strict);
        return mcp.RootElement.ValueKind == JsonValueKind.Array
            && mcp.RootElement.GetArrayLength() == 0
            && plugins.RootElement.GetProperty("installed").GetArrayLength() == 0;
    }

    public static IReadOnlyDictionary<string, string> EnvironmentFor(
        CodexProfile profile, string worktree)
    {
        var home = TrustedPaths.Directory(profile.Home);
        var tree = TrustedPaths.Directory(worktree);
        var exe = TrustedPaths.File(profile.Executable);
        TrustedPaths.Disjoint(home, tree);
        TrustedPaths.Disjoint(tree, exe);
        TrustedPaths.Disjoint(home, exe);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (TrustedPaths.Equal(home, user)
            || TrustedPaths.Equal(home, Path.Combine(user, ".codex")))
            throw new InvalidDataException("A dedicated runtime home is required.");
        TrustedPaths.OutsideRepository(home);
        TrustedPaths.OutsideRepository(Path.GetDirectoryName(exe)!);
        var system = Environment.SystemDirectory;
        var paths = new List<string> {system};
        foreach (var item in profile.ToolDirectories ?? [])
        {
            var directory = TrustedPaths.Directory(item);
            TrustedPaths.Disjoint(directory, tree);
            TrustedPaths.Disjoint(directory, home);
            TrustedPaths.OutsideRepository(directory);
            if (directory.Contains(Path.PathSeparator))
                throw new InvalidDataException("Ambiguous PATH entry.");
            paths.Add(directory);
        }
        var temp = Directory.CreateDirectory(Path.Combine(home, "tmp")).FullName;
        TrustedPaths.Directory(temp);
        var data = Directory.CreateDirectory(Path.Combine(home, "data")).FullName;
        TrustedPaths.Directory(data);
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CODEX_HOME"] = home, ["HOME"] = home, ["USERPROFILE"] = home,
                ["APPDATA"] = data, ["LOCALAPPDATA"] = data,
                ["TEMP"] = temp, ["TMP"] = temp,
                ["SystemRoot"] = Path.GetDirectoryName(system)!,
                ["SystemDrive"] = Path.GetPathRoot(system)![..2],
                ["ComSpec"] = Path.Combine(system, "cmd.exe"),
                ["PATH"] = string.Join(Path.PathSeparator, paths)
            });
    }
}
