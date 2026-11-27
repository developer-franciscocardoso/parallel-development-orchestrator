using System.Diagnostics;
using Pda.Agents;
using Pda.Core;
using Pda.Persistence;
using Pda.Repositories;

namespace Pda.Scheduling.Tests;

public sealed class DispatchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "pda-dispatch-" + Guid.NewGuid().ToString("N"));
    private readonly StateStore store;
    private readonly RepositoryBaseline baseline;
    private readonly TaskContract task = new("feature.one", "Implement a test",
        [], ["src"], [], [], ["Works"], [new("test", [])], ["Change"],
        Risk.Low, 1, 30, 0, "simulated", 20);

    public DispatchTests()
    {
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "repo");
        Directory.CreateDirectory(project);
        Git(project, "init", "-b", "main");
        Git(project, "-c", "user.name=PDA", "-c", "user.email=pda@invalid",
            "commit", "--no-gpg-sign", "--allow-empty", "-m", "baseline");
        baseline = new GitRepositoryService().Register(project);
        store = new StateStore(Path.Combine(root, "pda.db"));
        store.CreatePlan("test", project, "plan", [task], 50);
    }

    [Fact]
    public async Task SuccessStopsAtVerificationWithDurableEvidence()
    {
        var source = new SimulatedRuntime(SimulationScenario.Success);
        var scheduler = Coordinator(source);
        scheduler.Pause("plan");
        Assert.Null(await scheduler.RunNextAsync("plan"));
        scheduler.Resume("plan");
        var attempt = await scheduler.RunNextAsync("plan");
        Assert.Equal(AttemptState.AwaitingVerification, attempt!.State);
        Assert.Equal(13, attempt.UsedTokens);
        var files = store.Artifacts<DispatchRecord>("plan", "dispatch")
            .Single().Data;
        Assert.True(File.Exists(files.ReportPath));
        Assert.True(Directory.Exists(files.Worktree.Path));
        Assert.Empty(store.Artifacts<object>("plan", "worker"));
        Assert.Null(await scheduler.RunNextAsync("plan"));
    }

    [Fact]
    public async Task UnknownUsageIsBlocked()
    {
        var scheduler = Coordinator(new SimulatedRuntime(
            SimulationScenario.UnknownUsage));
        var result = await scheduler.RunNextAsync("plan");
        Assert.Equal(AttemptState.Blocked, result!.State);
        Assert.Null(result.UsedTokens);
    }

    [Fact]
    public async Task OtherControllerCanCancelRunningWorker()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var simulated = new SimulatedRuntime(SimulationScenario.Success,
            async (request, token) => {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            });
        var running = Coordinator(simulated).RunNextAsync("plan");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var other = Coordinator(simulated);
        var attempt = Assert.Single(other.Status("plan").Attempts);
        other.Cancel("plan", attempt.Id);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AttemptState.Cancelled, result!.State);
    }

    [Theory]
    [InlineData(SimulationScenario.Crash)]
    [InlineData(SimulationScenario.Timeout)]
    public async Task FailuresAreDurableAndRequireReconciliation(
        SimulationScenario scenario)
    {
        var scheduler = Coordinator(new SimulatedRuntime(scenario));
        var failed = await scheduler.RunNextAsync("plan");
        Assert.Equal(AttemptState.Failed, failed!.State);
        Assert.Null(failed.UsedTokens);
        Assert.Null(await scheduler.RunNextAsync("plan"));
        Assert.Equal("result", store.History("plan")
            .Where(e => e.Kind == "ArtifactStored")
            .Select(e => System.Text.Json.JsonDocument.Parse(e.Payload)
                .RootElement.GetProperty("kind").GetString()).Last());
    }

    private ExecutionCoordinator Coordinator(IAgentRuntime runtime)
    {
        var trees = Directory.CreateDirectory(Path.Combine(root, "trees"));
        var logs = Directory.CreateDirectory(Path.Combine(root, "logs"));
        return new(store, _ => runtime, plan => {
            var a = store.Reserve(plan, task.Id, Guid.NewGuid().ToString("N"));
            return a == null ? null : new DispatchAssignment(task, a, null);
        }, _ => baseline, trees.FullName, logs.FullName);
    }

    private static void Git(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = dir };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(root, "*",
                     SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
