using System.Diagnostics;
using System.Text.Json;

namespace Pda.Cli.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly DirectoryInfo temp =
        Directory.CreateTempSubdirectory("pda-update-cli-");
    private string Install => Path.Combine(temp.FullName, "install");
    private string Home => Path.Combine(temp.FullName, "unused-home");

    [Fact]
    public async Task StatusAndArgumentErrorsLeaveUserStateUntouched()
    {
        var status = await Update("--status", "--install-dir", Install);
        Assert.True(status.Code == 0, status.Body);
        Assert.False(JsonDocument.Parse(status.Body).RootElement
            .GetProperty("installed").GetBoolean());
        Assert.False(Directory.Exists(Home));
        var mixed = await Update("--status", "--rollback", "--install-dir", Install);
        Assert.Equal(2, mixed.Code);
        var noHash = await Update("--package", "no.zip", "--install-dir", Install);
        Assert.Equal(2, noHash.Code);
        Assert.False(Directory.Exists(Home));
    }

    [Fact]
    public async Task BootstrapInstallsAndRollsBackWithoutCreatingState()
    {
        var first = Package("2.0.0");
        var next = Package("2.0.1");
        async Task<(int Code, string Body)> InstallZip(string file, string hash) =>
            await Update("--package", file, "--sha256", hash, "--install-dir",
                Install);
        var initial = await InstallZip(first.Path, first.Digest);
        Assert.True(initial.Code == 0, initial.Body);
        var second = await InstallZip(next.Path, next.Digest);
        Assert.True(second.Code == 0, second.Body);
        Assert.Equal("2.0.1", JsonDocument.Parse(second.Body).RootElement
            .GetProperty("version").GetString());
        Assert.NotEqual(0, (await InstallZip(first.Path, new string('F', 64))).Code);
        var restored = await Update("--rollback", "--install-dir", Install);
        Assert.True(restored.Code == 0, restored.Body);
        var record = JsonDocument.Parse(restored.Body).RootElement;
        Assert.Equal("2.0.0", record.GetProperty("version").GetString());
        Assert.True(File.Exists(record.GetProperty("executable").GetString()));
        Assert.False(Directory.Exists(Home));
    }

    private (string Path, string Digest) Package(string version)
    {
        string path = Path.Combine(temp.FullName, version + ".zip");
        var bytes = System.Text.Encoding.UTF8.GetBytes("fixture " + version);
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes));
        using (var zip = System.IO.Compression.ZipFile.Open(path,
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var output = zip.CreateEntry("app/pda.exe").Open())
                output.Write(bytes);
            using var manifest = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(manifest, new { formatVersion = 1, version,
                entryPoint = "pda.exe", files = new[] { new { path = "pda.exe",
                    sha256 = digest } } });
        }
        using var file = File.OpenRead(path);
        return (path, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(file)));
    }

    private async Task<(int Code, string Body)> Update(params string[] args)
    {
        var executable = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "pda.exe" : "pda");
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] {"--home", Home, "--json", "update"}.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (process.ExitCode, (await output) + (await error));
    }

    public void Dispose()
    {
        if (temp.Exists) temp.Delete(true);
    }
}
