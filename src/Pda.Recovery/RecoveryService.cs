using System.Text.Json;
using Pda.Core;

namespace Pda.Recovery;

public sealed class RecoveryService(IStateStore store,
    IWorkerInspector inspector)
{
    public IReadOnlyList<Attempt> Recover(string planId)
    {
        var workers = store.Artifacts<WorkerRecord>(planId,
            WorkerRecord.ArtifactKind).ToDictionary(a => a.Id, a => a.Data);
        return store.Attempts(planId)
            .Select(a => RecoverAttempt(a, workers.GetValueOrDefault(a.Id)))
            .ToArray();
    }

    private Attempt RecoverAttempt(Attempt attempt, WorkerRecord? worker)
    {
        if (attempt.State is not (AttemptState.Reserved or
            AttemptState.Running)) return attempt;
        var observation = worker is null
            ? new WorkerObservation(TreeCondition.Unknown, false,
                "Startup interrupted before durable process identity.")
            : inspector.Inspect(worker);
        if (observation.Tree == TreeCondition.Live &&
            observation.RepositoryIntact) return attempt;
        // An absent process is never evidence of successful implementation.
        // Interrupted retains its reservation until explicit reconciliation.
        return store.Transition(attempt.Id, attempt.Version,
            AttemptState.Interrupted, JsonSerializer.Serialize(observation));
    }

    public Attempt Reconcile(string planId, string attemptId,
        long measuredTokens, string usageEvidence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(measuredTokens);
        ArgumentException.ThrowIfNullOrWhiteSpace(usageEvidence);
        var attempt = store.Attempts(planId).Single(a => a.Id == attemptId);
        var worker = store.Artifacts<WorkerRecord>(planId,
            WorkerRecord.ArtifactKind).SingleOrDefault(a => a.Id == attemptId);
        if (worker is null)
            throw new InvalidOperationException(
                "Missing identity requires external process reconciliation.");
        var observation = inspector.Inspect(worker.Data);
        if (observation.Tree != TreeCondition.Stopped)
            throw new InvalidOperationException("Process tree is unresolved.");
        if (attempt.State is AttemptState.Reserved or AttemptState.Running)
            attempt = store.Transition(attempt.Id, attempt.Version,
                AttemptState.Interrupted,
                JsonSerializer.Serialize(observation));
        var evidence = JsonSerializer.Serialize(new
        {
            observation, usageEvidence, measuredTokens
        });
        if (attempt.State == AttemptState.Failed &&
            attempt.UsedTokens == measuredTokens) return attempt;
        return store.Reconcile(attempt.Id, attempt.Version, evidence,
            measuredTokens, confirmedStopped: true);
    }
}
