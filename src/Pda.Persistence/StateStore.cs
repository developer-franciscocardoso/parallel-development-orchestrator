using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pda.Core;

namespace Pda.Persistence;

public sealed partial class StateStore(string path) : IStateStore
{
    private readonly Database database = new(path);

    public void CreatePlan(string projectId, string repository, string planId,
        IReadOnlyList<TaskContract> tasks, long tokenBudget)
    {
        ContractValidator.ValidateId(projectId);
        ContractValidator.ValidateId(planId);
        ArgumentOutOfRangeException.ThrowIfNegative(tokenBudget);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ContractValidator.Validate(tasks);
        var root = RepositoryRoot(repository);
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        db.Execute("INSERT INTO projects VALUES($p0,$p1) ON CONFLICT(id) DO NOTHING",
            tx, projectId, root);
        if (db.Number("SELECT count(*) FROM projects WHERE id=$p0 AND root=$p1",
            tx, projectId, root) != 1)
            throw new InvalidOperationException("Project root mismatch.");
        db.Execute("INSERT INTO plans VALUES($p0,$p1,$p2)", tx, planId,
            projectId, tokenBudget);
        foreach (var task in tasks)
            db.Execute("INSERT INTO tasks VALUES($p0,$p1,$p2)", tx,
                planId, task.Id, JsonSerializer.Serialize(task));
        foreach (var task in tasks)
            foreach (var dep in task.Dependencies)
                db.Execute("INSERT INTO dependencies VALUES($p0,$p1,$p2)",
                    tx, planId, task.Id, dep);
        Append(db, tx, planId, null, null, "PlanCreated",
            JsonSerializer.Serialize(new { projectId, root, tokenBudget, tasks }));
        tx.Commit();
    }

    public IReadOnlyList<TaskContract> Contracts(string planId)
    {
        using var db = database.Open();
        return db.Read("SELECT contract FROM tasks WHERE plan=$p0 ORDER BY id",
            r => JsonSerializer.Deserialize<TaskContract>(r.GetString(0))!,
            null, planId);
    }

    private static string RepositoryRoot(string repository)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        return OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
    }

    private const string Fields =
        "SELECT id,plan,task,number,state,version,reserved,used FROM attempts ";

    internal static Attempt Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
        Enum.Parse<AttemptState>(r.GetString(4)), r.GetInt64(5), r.GetInt64(6),
        r.IsDBNull(7) ? null : r.GetInt64(7));

    public IReadOnlyList<Attempt> Attempts(string planId)
    {
        using var db = database.Open();
        return db.Read(Fields + "WHERE plan=$p0 ORDER BY task,number", Map,
            null, planId);
    }

    private static void Append(SqliteConnection db, SqliteTransaction tx,
        string? plan, string? task, string? attempt, string kind, string payload)
        => db.Execute("""
            INSERT INTO journal(plan,task,attempt,kind,payload,timestamp)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5)
            """, tx, plan, task, attempt, kind, payload,
            DateTimeOffset.UtcNow.ToString("O"));

    public IReadOnlyList<JournalEntry> History(string? planId, long after = 0)
    {
        using var db = database.Open();
        return db.Read("""
            SELECT seq,plan,task,attempt,kind,payload,timestamp FROM journal
            WHERE plan IS $p0 AND seq>$p1 ORDER BY seq
            """, r => new JournalEntry(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.GetString(5), DateTimeOffset.Parse(r.GetString(6))),
            null, planId, after);
    }
}
