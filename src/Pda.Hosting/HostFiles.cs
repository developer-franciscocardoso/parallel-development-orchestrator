using System.Text.Json;
using System.Text.Json.Serialization;
using Pda.Security;

namespace Pda.Hosting;

internal static class HostFiles
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, MaxDepth = 12, WriteIndented = true
    };

    private const string DefaultProviders = """
        {
          "providers": [{
            "id":"local-simulation", "endpoint":"http://localhost",
            "maxConcurrentRequests":6, "requestsPerMinute":120,
            "tokensPerMinute":100000000
          }],
          "models": [{
            "id":"simulated", "providerId":"local-simulation",
            "runtimeIds":["simulated"], "contextTokens":1000000,
            "maxOutputTokens":1000000
          }]
        }
        """;

    internal static string DirectoryAt(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Supply an absolute local directory.");
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) return TrustedPaths.Directory(full);
        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidDataException("Missing state parent.");
        DirectoryAt(parent);
        TrustedPaths.OutsideRepository(parent);
        Directory.CreateDirectory(full);
        return TrustedPaths.Directory(full);
    }

    internal static HostSettings Initialize(string home)
    {
        var settings = Path.Combine(home, "host.json");
        WriteDefault(settings, JsonSerializer.Serialize(new HostSettings(), Json));
        WriteDefault(Path.Combine(home, "providers.json"), DefaultProviders);

        using var file = File.OpenRead(TrustedPaths.File(settings));
        if (file.Length is <= 0 or > 65536)
            throw new InvalidDataException("Invalid host configuration size.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1)
            throw new InvalidDataException("Host configuration changed.");
        var cfg = JsonSerializer.Deserialize<HostSettings>(bytes, Json)
            ?? throw new InvalidDataException("Missing host configuration.");
        if (cfg.DefaultConcurrency is < 1 or > 6 ||
            cfg.EnableLiveCodex && string.IsNullOrWhiteSpace(cfg.CodexModel))
            throw new InvalidDataException("Invalid concurrency or live model.");
        return cfg;
    }

    private static void WriteDefault(string target, string content)
    {
        if (File.Exists(target))
        {
            TrustedPaths.File(target);
            return;
        }
        var temp = target + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew))
            using (var writer = new StreamWriter(stream)) writer.Write(content);
            try { File.Move(temp, target); }
            catch (IOException) when (File.Exists(target))
            {
                TrustedPaths.File(target);
            }
        }
        finally { File.Delete(temp); }
    }

    internal static CodexProfile? Profile(string home, HostSettings config)
    {
        var file = config.CodexExecutable;
        if (file == null)
        {
            var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
            file = paths.Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
                .Select(p => Path.Combine(p, "codex.exe")).FirstOrDefault(File.Exists);
        }
        if (file == null)
        {
            if (config.EnableLiveCodex)
                throw new InvalidDataException("Configure a native Codex executable.");
            return null;
        }
        var executable = TrustedPaths.File(file);
        TrustedPaths.OutsideRepository(Path.GetDirectoryName(executable)!);
        string runtimeHome = config.CodexHome ?? Path.Combine(home, "codex-home");
        var profile = new CodexProfile(executable, DirectoryAt(runtimeHome),
            config.ToolDirectories, config.ReadOnlyDirectories);
        TrustedPaths.OutsideRepository(profile.Home);
        TrustedPaths.Disjoint(profile.Home, Path.Combine(home, "worktrees"));
        TrustedPaths.Disjoint(profile.Home, Path.Combine(home, "evidence"));
        return profile;
    }
}
