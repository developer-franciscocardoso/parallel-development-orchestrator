using System.Text.Json;
using Pda.Core;

namespace Pda.Persistence;

public sealed partial class StateStore
{
    public void RegisterProject(string id, string repository)
    {
        ContractValidator.ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var root = RepositoryRoot(repository);
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var fresh = db.Number("SELECT count(*) FROM projects WHERE id=$p0", tx, id) == 0;
        db.Execute("INSERT INTO projects VALUES($p0,$p1) ON CONFLICT(id) DO NOTHING",
            tx, id, root);
        if (db.Number("SELECT count(*) FROM projects WHERE id=$p0 AND root=$p1",
            tx, id, root) != 1)
            throw new InvalidOperationException("Project root mismatch.");
        if (fresh) Append(db, tx, null, null, null, "ProjectRegistered",
            JsonSerializer.Serialize(new ProjectInfo(id, root)));
        tx.Commit();
    }

    public IReadOnlyList<ProjectInfo> Projects()
    {
        using var db = database.Open();
        return db.Read("SELECT id,root FROM projects ORDER BY id",
            r => new ProjectInfo(r.GetString(0), r.GetString(1)), null);
    }

    public IReadOnlyList<PlanInfo> Plans()
    {
        using var db = database.Open();
        return db.Read("SELECT id,project,budget FROM plans ORDER BY id",
            r => new PlanInfo(r.GetString(0), r.GetString(1), r.GetInt64(2)),
            null);
    }

    public Artifact<T> PutArtifact<T>(string plan, string kind, string id,
        T data, long expectedVersion = 0)
    {
        ContractValidator.ValidateId(kind);
        ContractValidator.ValidateId(id);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        var json = JsonSerializer.Serialize(data);
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var version = db.Number("""
            SELECT coalesce(max(version),0) FROM artifacts
            WHERE plan=$p0 AND kind=$p1 AND id=$p2
            """, tx, plan, kind, id);
        if (expectedVersion != version)
            throw new InvalidOperationException("Stale artifact version.");
        db.Execute("""
            INSERT INTO artifacts VALUES($p0,$p1,$p2,$p3,$p4)
            ON CONFLICT(plan,kind,id) DO UPDATE SET
            data=excluded.data,version=excluded.version
            """, tx, plan, kind, id, json, checked(version + 1));
        var artifact = new Artifact<T>(id, data, version + 1);
        Append(db, tx, plan, null, null, "ArtifactStored",
            JsonSerializer.Serialize(new { kind, artifact }));
        tx.Commit();
        return artifact;
    }

    public IReadOnlyList<Artifact<T>> Artifacts<T>(string plan, string kind)
    {
        using var db = database.Open();
        return db.Read("""
            SELECT id,data,version FROM artifacts
            WHERE plan=$p0 AND kind=$p1 ORDER BY id
            """, r => new Artifact<T>(r.GetString(0),
                JsonSerializer.Deserialize<T>(r.GetString(1))!, r.GetInt64(2)),
            null, plan, kind);
    }

    public Attempt Reconcile(string attemptId, long expectedVersion,
        string evidence, long usedTokens, bool confirmedStopped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        ArgumentOutOfRangeException.ThrowIfNegative(usedTokens);
        if (!confirmedStopped)
            throw new InvalidOperationException("Process tree is unconfirmed.");
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var a = db.Read(Fields + "WHERE id=$p0", Map, tx, attemptId).Single();
        if (a.Version != expectedVersion || a.State is not (AttemptState.Interrupted
            or AttemptState.Failed or AttemptState.Cancelled or AttemptState.Blocked)
            || a.UsedTokens != null && a.UsedTokens != usedTokens)
            throw new InvalidOperationException("Invalid reconciliation.");
        var next = a with { Version = a.Version + 1, UsedTokens = usedTokens,
            State = a.State == AttemptState.Interrupted ? AttemptState.Failed : a.State };
        db.Execute("""
            UPDATE attempts SET state=$p0,version=$p1,used=$p2 WHERE id=$p3
            """, tx, next.State.ToString(), next.Version, usedTokens, a.Id);
        Append(db, tx, a.PlanId, a.TaskId, a.Id, "Reconciled",
            JsonSerializer.Serialize(new { next, evidence, confirmedStopped }));
        tx.Commit();
        return next;
    }

    public Attempt AuthorizeRetry(string attemptId, long expectedVersion,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var a = db.Read(Fields + "WHERE id=$p0", Map, tx, attemptId).Single();
        if (a.Version != expectedVersion || a.UsedTokens == null ||
            a.State is not (AttemptState.Failed or AttemptState.Cancelled) ||
            db.Number("SELECT count(*) FROM journal WHERE attempt=$p0 AND kind='Reconciled'",
                tx, a.Id) == 0)
            throw new InvalidOperationException("Retry requires reconciliation.");
        var next = a with { State = AttemptState.RetryAuthorized,
            Version = a.Version + 1 };
        db.Execute("UPDATE attempts SET state=$p0,version=$p1 WHERE id=$p2",
            tx, next.State.ToString(), next.Version, a.Id);
        Append(db, tx, a.PlanId, a.TaskId, a.Id, "RetryAuthorized",
            JsonSerializer.Serialize(new { next, reason }));
        tx.Commit();
        return next;
    }
}
