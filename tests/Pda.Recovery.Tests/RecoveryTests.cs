using Pda.Core;
using Pda.Persistence;

namespace Pda.Recovery.Tests;

public sealed class RecoveryTests : IDisposable
{
    private readonly DirectoryInfo folder =
        Directory.CreateTempSubdirectory("pda-recovery-");
    private readonly StateStore store;

    public RecoveryTests()
    {
        store = new StateStore(Path.Combine(folder.FullName, "state.db"));
        TaskContract task = new("task", "Implement", [], ["src"], [], [],
            ["tests pass"], [new("dotnet", ["test"])], ["source"],
            Risk.High, 2, 300, 1, "codex", 100);
        store.CreatePlan("project", folder.FullName, "plan", [task], 1000);
    }

    [Fact]
    public void MissingStartupEvidenceBecomesInterruptedOnlyOnce()
    {
        var attempt = store.Reserve("plan", "task", "request")!;
        var recovery = new RecoveryService(store,
            new Probe(TreeCondition.Stopped));
        var first = recovery.Recover("plan");
        var count = store.History("plan").Count;
        var second = recovery.Recover("plan");
        Assert.Equal(AttemptState.Interrupted, first.Single().State);
        Assert.Equal(AttemptState.Interrupted, second.Single().State);
        Assert.Equal(count, store.History("plan").Count);
        Assert.Null(store.Attempts("plan").Single().UsedTokens);
        Assert.Null(store.Reserve("plan", "task", "replacement"));
    }

    [Fact]
    public void LiveWorkerIsNeverReconciledAsStopped()
    {
        var attempt = Start();
        var recovery = new RecoveryService(store,
            new Probe(TreeCondition.Live));
        Assert.Equal(AttemptState.Running,
            recovery.Recover("plan").Single().State);
        Assert.Throws<InvalidOperationException>(() =>
            recovery.Reconcile("plan", attempt.Id, 10, "real log"));
        Assert.Equal(AttemptState.Running,
            store.Attempts("plan").Single().State);
    }

    [Fact]
    public void StoppedTreeNeedsKnownUsageAndExplicitRetryAuthorization()
    {
        var attempt = Start();
        var recovery = new RecoveryService(store,
            new Probe(TreeCondition.Stopped));
        recovery.Recover("plan");
        Assert.Null(store.Attempts("plan").Single().UsedTokens);
        var reconciled = recovery.Reconcile("plan", attempt.Id, 17,
            "Verified complete runtime usage event");
        Assert.Equal(AttemptState.Failed, reconciled.State);
        Assert.Equal(17, reconciled.UsedTokens);
        Assert.Null(store.Reserve("plan", "task", "replacement"));
        store.AuthorizeRetry(attempt.Id, reconciled.Version, "Fixed cause");
        Assert.NotNull(store.Reserve("plan", "task", "replacement"));
    }

    [Fact]
    public void HeldSimulationLeaseSurvivesRecoveryUntilReleased()
    {
        var task = store.Contracts("plan").Single() with { Runtime = "simulated" };
        store.CreatePlan("project", folder.FullName, "sim", [task], 1000);
        var attempt = store.Reserve("sim", "task", "sim-request")!;
        var path = SimulationLease.FilePath(folder.FullName, attempt.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var recovery = new RecoveryService(store,
            new Probe(TreeCondition.Unknown), folder.FullName);
        using (var lease = File.Create(path))
        {
            store.PutArtifact("sim", "simulation", attempt.Id,
                new SimulationLease(path));
            store.Transition(attempt.Id, attempt.Version, AttemptState.Running,
                "Simulator started");
            Assert.Equal(AttemptState.Running, recovery.Recover("sim").Single().State);
        }
        Assert.Equal(AttemptState.Interrupted, recovery.Recover("sim").Single().State);
        Assert.Equal(0, recovery.Reconcile("sim", attempt.Id, 0,
            "No simulated usage").UsedTokens);
    }

    private Attempt Start()
    {
        var attempt = store.Reserve("plan", "task", "request")!;
        store.PutArtifact("plan", WorkerRecord.ArtifactKind, attempt.Id,
            new WorkerRecord(attempt.Id, folder.FullName, "branch",
                new string('a', 40), 123, DateTimeOffset.UtcNow, "job",
                "stdout", "stderr"));
        return store.Transition(attempt.Id, attempt.Version,
            AttemptState.Running, "Process identity persisted before resume");
    }

    public void Dispose() => folder.Delete(true);

    private sealed class Probe(TreeCondition condition) : IWorkerInspector
    {
        public WorkerObservation Inspect(WorkerRecord worker) =>
            new(condition, true, "Synthetic external process boundary");
    }
}
