using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pda.Repositories;

internal static class LocalPaths
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool Equal(string left, string right)
        => left.Equals(right, Comparison);

    public static bool Overlaps(string left, string right)
        => Contains(left, right) || Contains(right, left);

    public static bool Contains(string directory, string path)
    {
        var prefix = Path.EndsInDirectorySeparator(directory)
            ? directory : directory + Path.DirectorySeparatorChar;
        return Equal(directory, path) || path.StartsWith(prefix, Comparison);
    }

    public static string DirectoryPath(string path)
    {
        var full = FullPath(path);
        if (!Directory.Exists(full))
            throw new GitRepositoryException("The directory must exist.");
        RejectReparsePoints(full);
        return OperatingSystem.IsWindows() ? WindowsDirectoryPath(full) : full;
    }

    public static string FullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new GitRepositoryException("An absolute path is required.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Any(char.IsControl))
            throw new GitRepositoryException("Path contains control text.");

        if (OperatingSystem.IsWindows() &&
            (full.StartsWith(@"\\?\", StringComparison.Ordinal) ||
             full.StartsWith(@"\\.\", StringComparison.Ordinal)))
            throw new GitRepositoryException("Device paths are unsupported.");

        return full;
    }

    public static void RequireAbsent(string path)
    {
        if (Attributes(path).HasValue)
            throw new GitRepositoryException("Attempt path already exists.");
    }

    public static void RequireOutsideCheckout(string directory)
    {
        for (string? current = directory; current is not null;
             current = Path.GetDirectoryName(current))
        {
            if (Attributes(Path.Combine(current, ".git")).HasValue)
                throw new GitRepositoryException(
                    "Attempt storage cannot be within an existing checkout.");
        }
    }

    public static void RejectReparsePoints(string path)
    {
        for (string? current = path; current is not null;
             current = Path.GetDirectoryName(current))
        {
            if (Attributes(current)?.HasFlag(FileAttributes.ReparsePoint)
                == true)
                throw new GitRepositoryException(
                    "Git paths cannot cross symlinks or junctions.");
        }
    }

    private static string WindowsDirectoryPath(string path)
    {
        const uint shareReadWriteDelete = 7;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using var handle = CreateFile(path, 0, shareReadWriteDelete,
            IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            throw WindowsPathError();

        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer,
            (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw WindowsPathError();

        var resolved = buffer.ToString();
        const string devicePrefix = @"\\?\";
        if (!resolved.StartsWith(devicePrefix, StringComparison.Ordinal) ||
            resolved.StartsWith(@"\\?\UNC\",
                StringComparison.OrdinalIgnoreCase))
            throw new GitRepositoryException("A local drive path is required.");

        return FullPath(resolved[devicePrefix.Length..]);
    }

    private static GitRepositoryException WindowsPathError() =>
        new("Could not resolve the directory path.",
            new Win32Exception(Marshal.GetLastWin32Error()));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName,
        uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,
        StringBuilder path, uint pathLength, uint flags);

    private static FileAttributes? Attributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception error) when (error is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
