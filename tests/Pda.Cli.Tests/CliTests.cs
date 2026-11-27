using System.Diagnostics;
using System.Text.Json;

namespace Pda.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "pda-cli-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HelpAndBadArgumentsDoNotInitializeStorage()
    {
        var (_, help) = await Run("--help");
        Assert.Contains("plan execute", help);
        var (code, failure) = await Run("--json", "project", "add");
        Assert.Equal(2, code);
        Assert.True(JsonDocument.Parse(failure).RootElement
            .TryGetProperty("error", out _));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task InitializationAndDiscoveryWorkAcrossProcesses()
    {
        Assert.Equal(0, (await Run("--json", "init")).Code);
        Assert.Equal(0, (await Run("--json", "init")).Code);
        Assert.Equal(0, (await Run("--json", "doctor")).Code);
        var repo = Directory.CreateDirectory(root + "-repo").FullName;
        Git(repo, "init", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "app.js"), "export default 1;");
        File.WriteAllText(Path.Combine(repo, "package.json"),
            "{\"scripts\":{\"test\":\"node --test\"}}");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "fixture");
        Assert.Equal(0, (await Run("project", "add", "demo", repo)).Code);
        Assert.Equal(0, (await Run("project", "inspect", "demo")).Code);
        var create = await Run("--json", "plan", "create", "demo", "plan",
            "--objective", "Update the source", "--budget", "100");
        Assert.True(create.Code == 0, create.Output);
        Assert.Equal(0, (await Run("plan", "validate", "plan")).Code);
        Assert.Equal(0, (await Run("task", "inspect", "plan", "T001")).Code);
        foreach (var command in new[] {"task list", "worker list",
            "review list", "integration list", "execution status",
            "execution history", "execution recover"})
        {
            var parts = command.Split(' ');
            var response = await Run("--json", parts[0], parts[1], "plan");
            Assert.True(response.Code == 0, response.Output);
            using var json = JsonDocument.Parse(response.Output);
        }
        Assert.Equal(2, (await Run("plan", "execute", "plan", "--typo")).Code);
    }

    [Fact]
    public async Task MigrationAndRestorePreserveExactEvidenceAndRejectOverwrite()
    {
        var legacy = Directory.CreateDirectory(root + "-repo").FullName;
        string At(string name) => Path.Combine(legacy, name);
        var original = System.Text.Encoding.UTF8.GetBytes(
            "{ \"schema_version\": 2, \"tasks\": [{\"attempts\":[" +
            "{\"status\":\"starting\",\"usage\":null}]}] }\n");
        File.WriteAllText(At("execution-state.json"), "{\"schema_version\":2}");
        File.WriteAllBytes(At("task-registry.json"), original);
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(original));
        Assert.Equal(0, (await Run("migrate", legacy)).Code);
        Assert.Equal(0, (await Run("migrate", legacy)).Code);
        Assert.Equal(original, File.ReadAllBytes(At("task-registry.json")));
        var listed = await Run("--json", "migration", "list");
        Assert.Equal(2, JsonDocument.Parse(listed.Output).RootElement
            .GetArrayLength());
        Assert.Contains(digest, listed.Output);
        var totals = At("totals.json");
        File.WriteAllText(totals, "[-1]");
        async Task<int> Reconcile() => (await Run("migration", "reconcile",
            At("task-registry.json"), digest, "--confirm-stopped", "--usage",
            totals, "--evidence", "fixture evidence")).Code;
        Assert.Equal(2, await Reconcile());
        File.WriteAllText(totals, "[11]");
        Assert.Equal(0, await Reconcile());
        var snapshot = At("snapshot.db");
        var restored = At("restored.db");
        Assert.Equal(0, (await Run("backup", snapshot)).Code);
        Assert.Equal(1, (await Run("backup", snapshot)).Code);
        Assert.Equal(0, (await Run("restore", snapshot, restored)).Code);
        Assert.Equal(1, (await Run("restore", snapshot, restored)).Code);
        Assert.Equal(1, (await Run("restore", totals, At("bad.db"))).Code);
        Assert.False(File.Exists(At("bad.db")));
        Assert.Equal(original, File.ReadAllBytes(At("task-registry.json")));
    }

    private async Task<(int Code, string Output)> Run(params string[] args)
    {
        var dll = typeof(CliApplication).Assembly.Location;
        var native = Path.Combine(Path.GetDirectoryName(dll)!,
            OperatingSystem.IsWindows() ? "pda.exe" : "pda");
        var start = new ProcessStartInfo(File.Exists(native) ? native : "dotnet") {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        if (!File.Exists(native)) start.ArgumentList.Add(dll);
        foreach (var arg in new[] {"--home", root}.Concat(args))
            start.ArgumentList.Add(arg);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        return (p.ExitCode, (await output) + (await error));
    }

    private static void Git(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = dir };
        foreach (var arg in new[] {"-c", "user.name=PDA", "-c",
            "user.email=pda@invalid", "-c", "commit.gpgsign=false"}.Concat(args))
            start.ArgumentList.Add(arg);
        using var p = Process.Start(start)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    public void Dispose()
    {
        foreach (var dir in new[] {root, root + "-repo"}.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*",
                         SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }
}
