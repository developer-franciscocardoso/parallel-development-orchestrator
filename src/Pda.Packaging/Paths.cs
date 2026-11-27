namespace Pda.Packaging;

internal static class Paths
{
    internal static string Local(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Format.Require(!full.StartsWith(@"\\") && !full.Any(char.IsControl) &&
            (!OperatingSystem.IsWindows() || (full.Length >= 3 &&
            full[1] == ':' && !full.AsSpan(2).Contains(':'))),
            "A local filesystem path is required.");
        Check(full);
        return full;
    }

    internal static void Check(string path)
    {
        for (string? current = path; current != null;
            current = Path.GetDirectoryName(current))
        {
            try
            {
                Format.Require(!File.GetAttributes(current)
                    .HasFlag(FileAttributes.ReparsePoint), "Linked installer path.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void MakeDirectory(string path)
    {
        Check(path);
        if (Directory.Exists(path)) return;
        MakeDirectory(Path.GetDirectoryName(path)
            ?? throw new DirectoryNotFoundException(path));
        Directory.CreateDirectory(path);
        Check(path);
    }

    internal static string Root(string path)
    {
        string root = Local(path);
        Format.Require(Path.GetDirectoryName(root) != null,
            "A dedicated installation directory is required.");
        for (var p = new DirectoryInfo(root); p != null; p = p.Parent)
            Format.Require(!Path.Exists(Path.Combine(p.FullName, ".git")) &&
                !File.Exists(Path.Combine(p.FullName, "state.db")),
                "Installation must be outside checkouts and orchestrator state.");
        return root;
    }
}
