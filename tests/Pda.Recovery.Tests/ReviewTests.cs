using Microsoft.Data.Sqlite;
using Pda.Core;
using Pda.Persistence;

namespace Pda.Recovery.Tests;

public sealed class ReviewTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory();
    private readonly StateStore store;
    private readonly RecoveryService recovery;
    private string Db => Path.Combine(root.FullName, "state.db");
    private readonly Attempt attempt;
    private readonly WorkerRecord worker;

    public ReviewTests()
    {
        store = new StateStore(Db);
        TaskContract task = new("t", "test", [], ["file"], [], [], ["pass"],
            [new("test", [])], ["file"], Risk.Low, 2, 30, 0, "sim", 10);
        store.CreatePlan("project", root.FullName, "p", [task], 30);
        attempt = store.Reserve("p", "t", "key")!;
        worker = new(attempt.Id, root.FullName, "branch", new string('a', 40),
            1, DateTimeOffset.UtcNow, "job", "out", "err");
        recovery = new(store, new Inspector(TreeCondition.Stopped));
    }

    [Fact]
    public void InterfaceTypedWritePreservesCompleteWorkerIdentity()
    {
        IWorkerIdentity identity = worker;
        store.PutArtifact("p", WorkerRecord.ArtifactKind, attempt.Id, identity);
        var stored = Assert.Single(store.Artifacts<WorkerRecord>(
            "p", WorkerRecord.ArtifactKind));
        Assert.Equal(worker, stored.Data);
        Assert.Throws<InvalidOperationException>(() => store.PutArtifact(
            "p", WorkerRecord.ArtifactKind, attempt.Id, identity, stored.Version));
        Assert.Equal(worker, Assert.Single(store.Artifacts<WorkerRecord>(
            "p", WorkerRecord.ArtifactKind)).Data);
    }
    [Theory]
    [InlineData(AttemptState.Failed)]
    [InlineData(AttemptState.Cancelled)]
    [InlineData(AttemptState.Blocked)]
    public void ReconciliationRecordsExactlyOneFactForEqualKnownUsage(
        AttemptState terminal)
    {
        store.PutArtifact("p", WorkerRecord.ArtifactKind, attempt.Id, worker);
        store.Transition(attempt.Id, attempt.Version, terminal, "ended", 5);
        var first = recovery.Reconcile("p", attempt.Id, 5, "observed");
        var second = recovery.Reconcile("p", attempt.Id, 5, "observed");
        Assert.Equal(first, second);
        Assert.Single(store.History("p"), e => e.Kind == "Reconciled");
        if (terminal == AttemptState.Failed)
            store.AuthorizeRetry(attempt.Id, second.Version, "resolved");
    }

    [Fact]
    public void WrongIdentityAndReplacementAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => store.PutArtifact(
            "p", WorkerRecord.ArtifactKind, attempt.Id,
            worker with { AttemptId = "other" }));
        store.PutArtifact("p", WorkerRecord.ArtifactKind, attempt.Id, worker);
        Assert.Throws<InvalidOperationException>(() => store.PutArtifact(
            "p", WorkerRecord.ArtifactKind, attempt.Id,
            worker with { ProcessId = 1234 }, 1));
    }

    [Fact]
    public void CorruptLegacyIdentityCannotReconcileTarget()
    {
        store.PutArtifact("p", WorkerRecord.ArtifactKind, attempt.Id, worker);
        using var raw = new SqliteConnection($"Data Source={Db};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = """
            UPDATE artifacts SET data=json_set(data,'$.AttemptId','other')
            WHERE kind='worker'
            """;
        cmd.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() =>
            recovery.Reconcile("p", attempt.Id, 4, "observed"));
        Assert.Equal(attempt, store.Attempts("p").Single());
    }

    [Fact]
    public void StartupGuardExcludesRecoveryUntilIdentityAndRunningCommit()
    {
        var dispatcher = new StateStore(Db);
        var live = new RecoveryService(store, new Inspector(TreeCondition.Live));
        using (dispatcher.AcquireStartupGuard())
        {
            Assert.Throws<IOException>(() => live.Recover("p"));
            Assert.Equal(attempt, store.Attempts("p").Single());
            dispatcher.PutArtifact("p", WorkerRecord.ArtifactKind, attempt.Id,
                worker);
            dispatcher.Transition(attempt.Id, attempt.Version,
                AttemptState.Running, "identity published");
        }
        Assert.Equal(AttemptState.Running, live.Recover("p").Single().State);
    }

    private sealed class Inspector(TreeCondition tree) : IWorkerInspector
    {
        public WorkerObservation Inspect(WorkerRecord worker) =>
            new(tree, true, "synthetic process observation");
    }

    public void Dispose() => root.Delete(true);
}
