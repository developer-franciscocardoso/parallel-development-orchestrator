using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pda.Packaging.Tests;

public sealed class PackageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory().FullName;
    private string InstallRoot => Path.Combine(root, "installed");
    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record FileSpec(string Name, string Text, string? Hash = null);
    private (string Path, string Hash) Zip(string version,
        FileSpec[]? payload = null, Action<ZipArchive>? extra = null)
    {
        payload ??= [new("pda.exe", version), new("data/config.json", "{}")];
        var manifest = new {formatVersion = 1, version, entryPoint = "pda.exe",
            files = payload.Select(p => new {path = p.Name,
                sha256 = p.Hash ?? Hash(Encoding.UTF8.GetBytes(p.Text))})};
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "manifest.json", JsonSerializer.Serialize(manifest));
            foreach (var f in payload) Add(zip, "app/" + f.Name, f.Text);
            extra?.Invoke(zip);
        }
        return (path, Hash(File.ReadAllBytes(path)));
    }

    private static ZipArchiveEntry Add(ZipArchive archive, string name,
        string value)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(value));
        return entry;
    }

    private Installation Install((string Path, string Hash) zip) =>
        PackageInstaller.Install(zip.Path, zip.Hash, InstallRoot);

    [Fact]
    public void InstallUpgradeRollbackAndRepeatRetainBothVersions()
    {
        Assert.Null(PackageInstaller.Status(InstallRoot));
        Assert.False(Directory.Exists(InstallRoot));
        var first = Zip("2.0.0");
        var original = Install(first);
        Assert.Equal(original, Install(first));
        var updated = Install(Zip("2.0.1"));
        Assert.Equal("2.0.0", updated.PreviousVersion);
        Assert.Equal("2.0.1", File.ReadAllText(updated.Executable));
        Assert.Equal(original.Version,
            PackageInstaller.Rollback(InstallRoot).Version);
        Assert.Equal(updated.Version,
            PackageInstaller.Rollback(InstallRoot).Version);
        Assert.Equal(updated, PackageInstaller.Status(InstallRoot));
    }

    [Fact]
    public void UnverifiedInputCannotInitializeOrSwitchARoot()
    {
        var first = Zip("2.0.0");
        Assert.Throws<InvalidDataException>(() => PackageInstaller.Install(
            first.Path, new string('f', 64), InstallRoot));
        Assert.False(Directory.Exists(InstallRoot));
        var before = Install(first);
        var bad = Zip("2.0.1", [new("pda.exe", "bad", new string('e',64))]);
        Assert.Throws<InvalidDataException>(() => Install(bad));
        Assert.Equal(before, PackageInstaller.Status(InstallRoot));
        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "versions", "2.0.1")));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("dir/../../evil")]
    [InlineData("C:/outside")]
    [InlineData("/absolute")]
    [InlineData("dir\\escape")]
    [InlineData("NUL.txt")]
    [InlineData("dir/alias. ")]
    [InlineData("safe:stream")]
    [InlineData(".pda-manifest.json")]
    [InlineData(".git/config")]
    public void RejectsUnsafeDeclaredNames(string path)
    {
        var archive = Zip("2.0.0", [new("pda.exe","good"), new(path,"bad")]);
        Assert.Throws<InvalidDataException>(() => Install(archive));
        Assert.False(Directory.Exists(InstallRoot));
    }

    [Theory]
    [InlineData("app/PDA.exe")]
    [InlineData("manifest.json")]
    [InlineData("unknown.txt")]
    [InlineData("app/not-in-manifest")]
    public void RejectsDuplicateOrUndeclaredArchiveMembers(string path)
    {
        var archive = Zip("2.0.0", extra: zip => Add(zip, path, "extra"));
        Assert.Throws<InvalidDataException>(() => Install(archive));
    }

    [Fact]
    public void RejectsFileDirectoryAliasesAndArchiveLinks()
    {
        var collision = Zip("2.0.0", [new("pda.exe","ok"),
            new("dir", "bad"), new("dir/nested.txt", "bad")]);
        Assert.Throws<InvalidDataException>(() => Install(collision));
        var casing = Zip("2.0.0", [new("pda.exe","ok"),
            new("dir/a", "bad"), new("Dir/b", "bad")]);
        Assert.Throws<InvalidDataException>(() => Install(casing));
        var symlink = Zip("2.0.0", extra: zip =>
        {
            var e = zip.CreateEntry("app/data/");
            e.ExternalAttributes = unchecked((int)0xa1ff0000);
        });
        Assert.Throws<InvalidDataException>(() => Install(symlink));
    }

    [Fact]
    public void PreservesCurrentWhenRollbackContentWasChanged()
    {
        var v1 = Install(Zip("2.0.0"));
        var v2 = Install(Zip("2.0.1"));
        var unrelated = Path.Combine(Path.GetDirectoryName(v1.Executable)!, "unexpected.dll");
        File.WriteAllText(unrelated, "unexpected");
        Assert.Throws<InvalidDataException>(() => PackageInstaller.Rollback(InstallRoot));
        Assert.Equal(v2, PackageInstaller.Status(InstallRoot));
        File.Delete(unrelated);
        File.AppendAllText(v1.Executable, "tampered");
        Assert.Throws<InvalidDataException>(() => PackageInstaller.Rollback(InstallRoot));
        Assert.Equal(v2, PackageInstaller.Status(InstallRoot));
    }

    [Fact]
    public void RejectsVersionCollisionAndOversizedDirectoryMetadata()
    {
        var old = Install(Zip("2.0.0"));
        var conflict = Zip("2.0.0", [new("pda.exe","different")]);
        Assert.Throws<InvalidDataException>(() => Install(conflict));
        var archive = Zip("2.0.1");
        var bytes = File.ReadAllBytes(archive.Path);
        bytes[^12] = 0xff;
        bytes[^11] = 0xff;
        File.WriteAllBytes(archive.Path, bytes);
        Assert.Throws<InvalidDataException>(() =>
            PackageInstaller.Install(archive.Path, Hash(bytes), InstallRoot));
        Assert.Equal(old, PackageInstaller.Status(InstallRoot));
    }

    [Fact]
    public void RejectsLinkedInstallationRoot()
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "target"));
        if (OperatingSystem.IsWindows())
        {
            var info = new ProcessStartInfo("cmd.exe") { WorkingDirectory = root,
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = {"/d", "/c", "mklink", "/j", "installed", "target"} };
            using var p = Process.Start(info)!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        else Directory.CreateSymbolicLink(InstallRoot, target.FullName);
        try
        {
            Assert.Throws<InvalidDataException>(() => Install(Zip("2.0.0")));
            Assert.Empty(target.EnumerateFileSystemInfos());
        }
        finally { Directory.Delete(InstallRoot); }
    }

    [Fact]
    public async Task ConcurrentUpdatesLeaveAValidatedActiveVersion()
    {
        Install(Zip("2.0.0"));
        var a = Zip("2.0.1");
        var b = Zip("2.0.2");
        var updates = await Task.WhenAll(Task.Run(() => Install(a)), Task.Run(() => Install(b)));
        var final = PackageInstaller.Status(InstallRoot)!;
        Assert.Contains(final, updates);
        Assert.Contains(final.PreviousVersion, new[] {"2.0.1", "2.0.2"});
        Assert.Equal(final.PreviousVersion, PackageInstaller.Rollback(InstallRoot).Version);
    }

    public void Dispose() => Directory.Delete(root, true);
}
