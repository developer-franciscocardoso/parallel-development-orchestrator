using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pda.Packaging;

public static class PackageInstaller
{
    public static Installation Install(string zipPath, string expectedSha256,
        string installRoot)
    {
        string root = Paths.Root(installRoot);
        using var source = new FileStream(Paths.Local(zipPath), FileMode.Open,
            FileAccess.Read, FileShare.Read);
        using var package = new Package(source, expectedSha256);
        Paths.MakeDirectory(root);
        using var gate = Lock(root);
        var before = ReadPointer(root);
        var version = package.Release;
        var current = before?.Current;
        if (current?.Version == version.Version)
        {
            Format.Require(current == version,
                "An installed version has a different package identity.");
            Verify(root, current);
            return Result(root, before!);
        }
        if (before?.Previous?.Version == version.Version)
            Format.Require(before.Previous == version, "Previous version collision.");
        if (current != null) Verify(root, current);
        var versions = Path.Combine(root, "versions");
        Paths.MakeDirectory(versions);
        string destination = Path.Combine(versions, version.Version);
        Paths.Check(destination);
        if (!Directory.Exists(destination)) Stage(package, root, destination);
        Verify(root, version);
        var next = new Pointer(1, version, current);
        Switch(root, next);
        return Result(root, next);
    }

    public static Installation Rollback(string installRoot)
    {
        string root = Paths.Root(installRoot);
        if (!Directory.Exists(root))
            throw new InvalidOperationException("No installed version.");
        using var gate = Lock(root);
        var before = ReadPointer(root);
        var previous = before?.Previous ??
            throw new InvalidOperationException("No previous version.");
        Verify(root, previous);
        var next = new Pointer(1, previous, before!.Current);
        Switch(root, next);
        return Result(root, next);
    }

    public static Installation? Status(string installRoot)
    {
        string root = Paths.Root(installRoot);
        var marker = Path.Combine(root, "current.json");
        Paths.Check(marker);
        if (!Directory.Exists(root) || !File.Exists(marker))
            return null;
        using var gate = Lock(root);
        var pointer = ReadPointer(root);
        if (pointer == null) return null;
        Verify(root, pointer.Current);
        return Result(root, pointer);
    }

    private static FileStream Lock(string root)
    {
        var path = Path.Combine(root, ".install.lock");
        var started = Stopwatch.StartNew();
        while (true)
        {
            Paths.Check(path);
            try { return new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 11 or 32 or 33 &&
                started.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(40);
            }
        }
    }

    private static Installation Result(string root, Pointer state) =>
        new(root, state.Current.Version, Path.Combine(root, "versions",
            state.Current.Version, state.Current.EntryPoint),
            state.Previous?.Version, state.Current.PackageSha256);

    private static Pointer? ReadPointer(string root)
    {
        var path = Path.Combine(root, "current.json");
        Paths.Check(path);
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        var state = Format.Parse<Pointer>(Format.Limited(file, file.Length, 8192));
        Format.Require(state.FormatVersion == 1 && state.Current != null,
            "Invalid installation pointer.");
        Validate(state.Current!);
        if (state.Previous != null)
        {
            Validate(state.Previous);
            Format.Require(state.Current!.Version != state.Previous.Version,
                "Invalid previous installation.");
        }
        return state;
    }

    private static void Validate(Release release)
    {
        Format.Version(release.Version);
        Format.Require(release.EntryPoint == "pda.exe" &&
            Format.Hash(release.PackageSha256) && Format.Hash(release.ManifestSha256),
            "Invalid installed release identity.");
    }

    private static void Stage(Package package, string root, string destination)
    {
        var stage = Path.Combine(root, ".pda-stage-" + Guid.NewGuid().ToString("N"));
        Format.Require(!Path.Exists(stage), "Staging directory already exists.");
        Paths.MakeDirectory(stage);
        try
        {
            package.Extract(stage);
            Directory.Move(stage, destination);
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                Paths.Check(stage);
                Directory.Delete(stage, recursive: true);
            }
        }
    }

    private static void Switch(string root, Pointer state)
    {
        var path = Path.Combine(root, "current.json");
        Paths.Check(path);
        var temp = Path.Combine(root, ".pda-pointer-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(file, state, Format.Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static void Verify(string root, Release release)
    {
        var tree = Path.Combine(root, "versions", release.Version);
        string path = Path.Combine(tree, Format.SavedManifest);
        Paths.Check(path);
        using var metadata = File.OpenRead(path);
        var bytes = Format.Limited(metadata, metadata.Length, Format.MaxManifest);
        Format.Require(Format.Match(release.ManifestSha256,
            Convert.ToHexString(SHA256.HashData(bytes))), "Installed manifest changed.");
        var manifest = Format.ReadManifest(bytes);
        Format.Require(manifest.Version == release.Version,
            "Installed version differs from pinned release.");
        var files = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var expectedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in files.Keys)
        {
            for (var slash = name.IndexOf('/'); slash >= 0;
                slash = name.IndexOf('/', slash + 1))
                expectedDirs.Add(name[..slash]);
        }
        var directories = new Stack<string>();
        directories.Push(tree);
        long total = 0;
        int entries = 0;
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                Paths.Check(entry);
                Format.Require(++entries <= 2 * Format.MaxFiles + 1,
                    "Installed tree exceeds limits.");

                var name = Path.GetRelativePath(tree, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (Directory.Exists(entry))
                {
                    Format.Require(expectedDirs.Contains(name),
                        "Unexpected directory in installed version.");
                    directories.Push(entry);
                    continue;
                }
                if (name == Format.SavedManifest) continue;
                Format.Require(files.Remove(name, out var item),
                    "Unexpected file in installed version.");
                using var file = File.OpenRead(entry);
                Format.Require(file.Length <= Format.MaxFile &&
                    (total += file.Length) <= Format.MaxExpanded &&
                    Format.Match(Convert.ToHexString(SHA256.HashData(file)),
                        item!.Sha256), "Installed payload changed.");
            }
        }
        Format.Require(files.Count == 0, "Installed payload is incomplete.");
    }
}
