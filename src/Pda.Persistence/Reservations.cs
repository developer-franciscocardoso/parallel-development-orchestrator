using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pda.Core;

namespace Pda.Persistence;

public sealed partial class StateStore
{
    private const string Active =
        "('Reserved','Running','AwaitingVerification','Verified','Interrupted')";

    public Attempt? Reserve(string planId, string taskId, string requestKey,
        int concurrency = 6)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        if (concurrency is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var prior = db.Read(Fields + "WHERE request=$p0", Map, tx, requestKey)
            .SingleOrDefault();
        if (prior != null && (prior.PlanId != planId || prior.TaskId != taskId ||
            db.Number("SELECT requested_limit FROM attempts WHERE request=$p0",
                tx, requestKey) != concurrency))
            throw new InvalidOperationException("Conflicting reservation key.");
        if (prior != null) return prior;
        var task = db.Read("SELECT contract FROM tasks WHERE plan=$p0 AND id=$p1",
            r => JsonSerializer.Deserialize<TaskContract>(r.GetString(0))!,
            tx, planId, taskId).Single();
        if (!HasCapacity(db, tx, planId, task, concurrency)) return null;
        var attempts = db.Read(Fields + "WHERE plan=$p0 AND task=$p1",
            Map, tx, planId, taskId);
        if (attempts.Count >= task.MaxAttempts ||
            attempts.Any(a => a.State != AttemptState.RetryAuthorized))
            return null;
        var record = new Attempt(Guid.NewGuid().ToString("N"), planId, taskId,
            attempts.Count + 1, AttemptState.Reserved, 1, task.TokenReservation,
            null);
        db.Execute("""
            INSERT INTO attempts(id,plan,task,number,state,version,reserved,request,requested_limit)
            VALUES($p0,$p1,$p2,$p3,'Reserved',1,$p4,$p5,$p6)
            """, tx, record.Id, planId, taskId, record.Number,
            record.ReservedTokens, requestKey, concurrency);
        Append(db, tx, planId, taskId, record.Id, "Reserved",
            JsonSerializer.Serialize(record));
        tx.Commit();
        return record;
    }

    private static bool HasCapacity(SqliteConnection db, SqliteTransaction tx,
        string plan, TaskContract task, int concurrency)
    {
        if (db.Number($"SELECT count(*) FROM attempts WHERE state IN ('Reserved','Running','AwaitingVerification','Verified','Interrupted')",
            tx) >= concurrency) return false;
        if (db.Number("""
            SELECT count(*) FROM dependencies d WHERE plan=$p0 AND task=$p1
            AND NOT EXISTS(SELECT 1 FROM attempts a WHERE a.plan=d.plan
            AND a.task=d.dependency AND a.state='Integrated')
            """, tx, plan, task.Id) != 0) return false;
        if (db.Number($"""
            SELECT count(*) FROM attempts WHERE plan=$p0 AND used IS NULL
            AND state NOT IN ('Running','Reserved')
            """, tx, plan) != 0) return false;
        if (db.Number("SELECT count(*) FROM attempts WHERE plan=$p0 AND state='Interrupted'", tx, plan) != 0) return false;
        var remaining = db.Number("""
            SELECT budget - (SELECT coalesce(sum(coalesce(used,reserved)),0)
            FROM attempts WHERE plan=$p0) FROM plans WHERE id=$p0
            """, tx, plan);
        if (remaining < task.TokenReservation) return false;
        var siblings = db.Read($"""
            SELECT t.contract FROM attempts a JOIN tasks t
            ON a.plan=t.plan AND a.task=t.id JOIN plans p ON a.plan=p.id
            WHERE p.project=(SELECT project FROM plans WHERE id=$p0)
            AND a.state IN {Active}
            """, r => JsonSerializer.Deserialize<TaskContract>(r.GetString(0))!,
            tx, plan);
        return !siblings.Any(s => ContractValidator.Conflicts(task, s));
    }

    public Attempt Transition(string attemptId, long expectedVersion,
        AttemptState state, string evidence, long? usedTokens = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        if (usedTokens < 0) throw new ArgumentOutOfRangeException(nameof(usedTokens));
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var current = db.Read(Fields + "WHERE id=$p0", Map, tx, attemptId).Single();
        if (current.Version != expectedVersion)
            throw new InvalidOperationException("Stale attempt version.");
        if ((state == AttemptState.Running && usedTokens.HasValue) ||
            state == AttemptState.RetryAuthorized || !Permitted(current.State, state))
            throw new InvalidOperationException("Invalid attempt transition.");
        if (state is AttemptState.Verified or AttemptState.Integrated &&
            current.UsedTokens == null && usedTokens == null)
            throw new InvalidOperationException("Usage requires reconciliation.");
        if (current.UsedTokens.HasValue && usedTokens.HasValue &&
            current.UsedTokens != usedTokens)
            throw new InvalidOperationException("Conflicting final usage.");
        var updated = current with { State = state, Version = expectedVersion + 1,
            UsedTokens = usedTokens ?? current.UsedTokens };
        db.Execute("""
            UPDATE attempts SET state=$p0,version=$p1,used=$p2 WHERE id=$p3
            """, tx, state.ToString(), updated.Version, updated.UsedTokens, attemptId);
        Append(db, tx, current.PlanId, current.TaskId, attemptId,
            state.ToString(), JsonSerializer.Serialize(new { updated, evidence }));
        tx.Commit();
        return updated;
    }

    private static bool Permitted(AttemptState from, AttemptState to) =>
        from switch
        {
            AttemptState.Reserved => to == AttemptState.Running || Stop(to),
            AttemptState.Running => to == AttemptState.AwaitingVerification || Stop(to),
            AttemptState.AwaitingVerification => to == AttemptState.Verified || Stop(to),
            AttemptState.Verified => to == AttemptState.Integrated || Stop(to),
            _ => false
        };

    private static bool Stop(AttemptState to) => to is AttemptState.Failed
        or AttemptState.Cancelled or AttemptState.Blocked or AttemptState.Interrupted;
}
