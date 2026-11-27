using Microsoft.Data.Sqlite;
using Pda.Core;

namespace Pda.Persistence.Tests;

public sealed class ReviewTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory();
    private string At(string name) => Path.Combine(root.FullName, name);
    public void Dispose() => root.Delete(true);

    private void Create(StateStore db, string id) =>
        db.CreatePlan(id, Directory.CreateDirectory(At(id)).FullName, id, [new TaskContract(id, "work", [],
            ["same/path"], [], [], ["ok"], [new("test", [])], ["file"],
            Risk.Low, 1, 60, 0, "sim", 1)], 1);

    [Fact]
    public void SeparateProjectIdsCannotBypassOverlappingPaths()
    {
        var db = new StateStore(At("state.db"));
        Create(db, "p");
        Create(db, "alias");
        Assert.NotNull(db.Reserve("p", "p", "1"));
        Assert.Null(db.Reserve("alias", "alias", "2"));
    }

    [Fact]
    public void ImportedRiskBlocksAllDispatchUntilReconciled()
    {
        var db = new StateStore(At("state.db"));
        Create(db, "p");
        File.WriteAllText(At("execution-state.json"), """{"schema_version":2}""");
        File.WriteAllText(At("task-registry.json"), """
            {"schema_version":2,"tasks":[{"attempts":[{"status":"running",
            "usage":null}]}]}
            """);
        var registry = db.ImportLegacy(root.FullName).Single(e =>
            e.Source == At("task-registry.json"));
        Assert.Null(db.Reserve("p", "p", "1"));
        Assert.Throws<InvalidOperationException>(() =>
            db.ReconcileLegacy(registry.Source, registry.Sha256,
                false, [3], "still alive"));
        Assert.Throws<InvalidOperationException>(() =>
            db.ReconcileLegacy(registry.Source, registry.Sha256,
                true, [], "missing measurement"));
        db.ReconcileLegacy(registry.Source, registry.Sha256,
            true, [3], "process tree closed, log confirmed three tokens");
        db.ImportLegacy(root.FullName);
        Assert.NotNull(new StateStore(At("state.db")).Reserve("p", "p", "1"));
    }

    [Theory]
    [InlineData("DROP TRIGGER journal_no_delete")]
    [InlineData("DROP INDEX active_assignment")]
    [InlineData("DROP TABLE artifacts")]
    [InlineData("DROP TRIGGER journal_no_update; CREATE TRIGGER journal_no_update BEFORE UPDATE ON journal BEGIN SELECT 1; END;")]
    public void RestoreRejectsWeakenedSchema(string damage)
    {
        new StateStore(At("state.db")).Backup(At("backup.db"));
        using (var raw = new SqliteConnection(
            $"Data Source={At("backup.db")};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = damage;
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() =>
            StateStore.Restore(At("backup.db"), At("restored.db")));
        Assert.False(File.Exists(At("restored.db")));
    }
}
