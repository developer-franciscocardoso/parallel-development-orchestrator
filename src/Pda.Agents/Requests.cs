using Pda.Security;

namespace Pda.Agents;

internal static class Requests
{
    internal static void Validate(AgentRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AttemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request.Timeout));
        var tree = TrustedPaths.Directory(request.Worktree);
        var files = new[] {request.OutputLog, request.ErrorLog,
            request.ReportPath}.Select(p => TrustedPaths.FreshFile(p, tree));
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new InvalidDataException("Evidence paths must be distinct.");
    }

    internal static void CheckEvidence(AgentRequest request, CodexProfile profile)
    {
        var dirs = (profile.ToolDirectories ?? [])
            .Concat(profile.ReadOnlyDirectories ?? [])
            .Append(profile.Home).Append(Path.GetDirectoryName(profile.Executable)!);
        foreach (string dir in dirs)
        {
            foreach (string path in new[] {request.OutputLog, request.ErrorLog,
                request.ReportPath})
                TrustedPaths.Disjoint(Path.GetFullPath(dir), Path.GetFullPath(path));
        }
    }

    internal static void CheckConfig(string tree, string home)
    {
        // Diagnostics and exec must agree on which config layers are active.
        if (File.Exists(Path.Combine(home, "config.toml")))
            throw new InvalidDataException("Runtime home must be configuration-free.");
        for (var dir = new DirectoryInfo(tree); dir != null; dir = dir.Parent)
        {
            var cfg = Path.Combine(dir.FullName, ".codex", "config.toml");
            if (Path.Exists(cfg))
                throw new InvalidDataException("Found project configuration.");
            if (Path.Exists(Path.Combine(dir.FullName, ".git"))) return;
        }
        throw new InvalidDataException("A Git worktree is required.");
    }
}
