namespace Pda.Security;

public static class TrustedPaths
{
    public static string Directory(string path)
    {
        string full = Canonical(path);
        if (!System.IO.Directory.Exists(full))
            throw new DirectoryNotFoundException("Runtime directory missing.");
        return full;
    }

    public static string File(string path)
    {
        string full = Canonical(path);
        if (!System.IO.File.Exists(full))
            throw new FileNotFoundException("Trusted executable missing.");
        return full;
    }

    public static string FreshFile(string path, string worktree)
    {
        string full = Canonical(path);
        Directory(Path.GetDirectoryName(full)!);
        Disjoint(worktree, full);
        OutsideRepository(Path.GetDirectoryName(full)!);
        if (Path.Exists(full))
            throw new IOException("Attempt evidence already exists.");
        return full;
    }

    public static void OutsideRepository(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
            if (Path.Exists(Path.Combine(dir.FullName, ".git")))
                throw new InvalidDataException("Trusted state overlaps a checkout.");
    }

    public static bool Equal(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static void Disjoint(string a, string b)
    {
        if (Contains(a, b) || Contains(b, a))
            throw new InvalidDataException("Trusted and worker paths overlap.");
    }

    private static bool Contains(string a, string b) => Equal(a, b) ||
        b.StartsWith((Path.EndsInDirectorySeparator(a) ? a
            : a + Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")
            || path.Any(char.IsControl))
            throw new InvalidDataException("An absolute local path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows()
            && (full.Length < 3 || !char.IsAsciiLetter(full[0])
                || full[1] != ':' || full[2] != Path.DirectorySeparatorChar))
            throw new InvalidDataException("Expected a local drive path.");
        var segments = full[(Path.GetPathRoot(full)?.Length ?? 0)..]
            .Split(Path.DirectorySeparatorChar);
        if (segments.Any(p => p.Contains('~') || p.Contains(':')
            || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("Ambiguous path components.");
        for (string? p = full; p != null; p = Path.GetDirectoryName(p))
        {
            try
            {
                if (System.IO.File.GetAttributes(p).HasFlag(
                    FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Reparse points are forbidden.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }
}
