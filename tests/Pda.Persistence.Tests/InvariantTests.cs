using Microsoft.Data.Sqlite;
using Pda.Core;

namespace Pda.Persistence.Tests;

public sealed class InvariantTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory();
    private readonly StateStore store;
    private string FileAt(string name) => Path.Combine(root.FullName, name);
    public InvariantTests() => store = new StateStore(FileAt("state.db"));
    public void Dispose() => root.Delete(true);

    private static TaskContract Work(string id, params string[] deps) =>
        new(id, "Build", deps, [id], [], [], ["passes"], [new("test", [])],
            ["file"], Risk.Low, 2, 60, 5, "sim", 10);

    private void Plan(params TaskContract[] tasks) =>
        store.CreatePlan("project", root.FullName, "p", tasks, 20);

    [Fact]
    public void RequiresIntegratedDependencyAndVersion()
    {
        Plan(Work("a"), Work("b", "a"));
        var a = store.Reserve("p", "a", "key")!;
        Assert.Equal(a, store.Reserve("p", "a", "key"));
        Assert.Throws<InvalidOperationException>(() =>
            store.Reserve("p", "b", "key"));
        foreach (var state in new[] { AttemptState.Running,
            AttemptState.AwaitingVerification, AttemptState.Verified,
            AttemptState.Integrated })
        {
            Assert.Null(store.Reserve("p", "b", "another"));
            Assert.Throws<InvalidOperationException>(() =>
                store.Transition(a.Id, 0, state, "evidence"));
            a = store.Transition(a.Id, a.Version, state, "evidence",
                state == AttemptState.Running ? null : 10);
        }
        Assert.NotNull(store.Reserve("p", "b", "another"));
        Assert.Equal(7, store.History("p").Count);
    }

    [Fact]
    public void UnknownUsageAndUnconfirmedProcessPreventRetry()
    {
        Plan(Work("a"), Work("b"));
        var a = store.Reserve("p", "a", "start")!;
        a = store.Transition(a.Id, a.Version, AttemptState.Interrupted, "crash");
        Assert.Null(store.Reserve("p", "b", "new"));
        Assert.Throws<InvalidOperationException>(() =>
            store.Reconcile(a.Id, a.Version, "missing", 3, false));
        Assert.Throws<InvalidOperationException>(() =>
            store.AuthorizeRetry(a.Id, a.Version, "retry"));
        a = store.Reconcile(a.Id, a.Version, "tree stopped; log inspected", 3, true);
        Assert.Null(store.Reserve("p", "a", "retry"));
        store.AuthorizeRetry(a.Id, a.Version, "retry once");
        Assert.Equal(2, store.Reserve("p", "a", "retry")!.Number);
        Assert.Null(store.Reserve("p", "b", "budget"));
    }

    [Fact]
    public async Task SixSlotsAreGlobalAcrossPlans()
    {
        var ids = Enumerable.Range(0, 12).Select(i => $"p{i}").ToList();
        foreach (var id in ids)
            store.CreatePlan("project", root.FullName, id, [Work(id)], 10);
        var admissions = await Task.WhenAll(ids.Select(id => Task.Run(() =>
            new StateStore(FileAt("state.db")).Reserve(id, id, id))));
        Assert.Equal(6, admissions.Count(a => a != null));
    }

    [Fact]
    public void JournalFailureRollsBackAdmissionAndAppendOnlyTriggersRejectEdits()
    {
        Plan(Work("a"));
        using var db = new SqliteConnection(
            $"Data Source={FileAt("state.db")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TRIGGER fault BEFORE INSERT ON journal BEGIN
            SELECT RAISE(ABORT,'simulated failure'); END;
            """;
        cmd.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.Reserve("p", "a", "key"));
        Assert.Empty(store.Attempts("p"));
        Assert.Single(store.History("p"));
        cmd.CommandText = "DELETE FROM journal";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        cmd.CommandText = "UPDATE journal SET kind='fake'";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void BackupRestorePreservesVersionedEvidenceAndNeverOverwrites()
    {
        Plan(Work("a"));
        store.PutArtifact("p", "profile", "id", 1);
        store.PutArtifact("p", "profile", "id", 2, 1);
        Assert.Throws<InvalidOperationException>(() =>
            store.PutArtifact("p", "profile", "id", 3, 1));
        store.Backup(FileAt("backup.db"));
        StateStore.Restore(FileAt("backup.db"), FileAt("copy.db"));
        var copy = new StateStore(FileAt("copy.db"));
        copy.CheckIntegrity();
        Assert.Equal(store.History("p"), copy.History("p"));
        Assert.Equal(2, Assert.Single(copy.Artifacts<int>("p", "profile")).Data);
        Assert.Throws<IOException>(() => store.Backup(FileAt("copy.db")));
    }

    [Fact]
    public void LegacyImportsPreserveBytesAndUnknownUsage()
    {
        File.WriteAllText(FileAt("execution-state.json"), """{"schema_version":2}""");
        var registry = FileAt("task-registry.json");
        File.WriteAllText(registry, """
            {"schema_version":2,"tasks":[{"attempts":[{"status":"running",
            "usage":null}]}]}
            """);
        var original = File.ReadAllBytes(registry);
        store.ImportLegacy(root.FullName);
        store.ImportLegacy(root.FullName);
        Assert.Equal(2, store.ImportedEvidence().Count);
        var report = store.ImportedEvidence().Single(r => r.Source == registry);
        Assert.Equal(1, report.UnknownAttempts);
        Assert.Equal(1, report.ActiveAttempts);
        Assert.Equal(original, store.ReadLegacyBytes(registry, report.Sha256));
        Assert.Equal(original, File.ReadAllBytes(registry));
        Assert.Empty(store.Plans());
    }
}
