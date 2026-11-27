using System.Diagnostics;
using Pda.Agents;
using Pda.Core;
using Pda.Persistence;
using Pda.Planning;
using Pda.Providers;

namespace Pda.Application.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public async Task DiscoveredPlanDispatchesIntoDedicatedGitTree()
    {
        string Folder(string name) =>
            Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var repo = Folder("repo");
        Git(repo, "init", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "hello.cs"), "// initial");
        Git(repo, "add", "hello.cs");
        Git(repo, "-c", "user.name=Test", "-c", "user.email=test@invalid",
            "commit", "--no-gpg-sign", "-m", "fixture");
        var store = new StateStore(Path.Combine(root, "state.db"));
        var planning = new PlanningService(store);
        planning.InspectAndRegister("project", repo);
        planning.Generate("project", "plan", "Update greeting", ["hello.cs"],
            "simulated", 30, [new("test", [])]);
        var path = Path.Combine(root, "providers.json");
        File.WriteAllText(path, """
            {"providers":[{"id":"fixture","endpoint":"https://example.com",
            "maxConcurrentRequests":2,"requestsPerMinute":10,
            "tokensPerMinute":500}],"models":[{"id":"fixture",
            "providerId":"fixture","runtimeIds":["simulated"],
            "contextTokens":256,"maxOutputTokens":32}]}
            """);
        var runtime = new SimulatedRuntime(SimulationScenario.Success,
            (r, _) => {
                File.WriteAllText(Path.Combine(r.Worktree, "hello.cs"), "// new");
                return Task.FromResult(new WorkerReport(r.TaskId, r.AttemptId,
                    "completed", "Updated", "", ["Updated"], [], []));
            });
        var trees = Folder("trees");
        var evidence = Folder("evidence");
        var workers = ExecutionServices.Create(store,
            ProviderRegistry.Load(path, ["simulated"]), [runtime],
            _ => "fixture", trees, evidence, 2,
            () => new(1, 1, 4L << 30, 100));
        var result = await workers.RunNextAsync("plan");
        Assert.Equal(AttemptState.AwaitingVerification, result?.State);
        Assert.Equal("// initial", File.ReadAllText(Path.Combine(repo, "hello.cs")));
        var record = store.Artifacts<Pda.Scheduling.DispatchRecord>(
            "plan", "dispatch").Single().Data;
        Assert.Equal("// new", File.ReadAllText(
            Path.Combine(record.Worktree.Path, "hello.cs")));
        Assert.NotEqual(repo, record.Worktree.Baseline.Repository.Root);
    }

    private static void Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in Directory.EnumerateFiles(root, "*",
            SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
