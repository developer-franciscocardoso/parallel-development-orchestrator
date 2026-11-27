using System.Text.Json;
using Pda.Core;

namespace Pda.Recovery;

public sealed class RecoveryService(IStateStore store,
    IWorkerInspector inspector, string? evidenceRoot = null)
{
    public IReadOnlyList<Attempt> Recover(string planId)
    {
        using var guard = store.AcquireStartupGuard();
        var attempts = store.Attempts(planId);
        var workers = store.Artifacts<WorkerRecord>(planId,
            WorkerRecord.ArtifactKind).ToDictionary(a => a.Id, a => a.Data);
        var leases = store.Artifacts<SimulationLease>(planId, "simulation")
            .ToDictionary(a => a.Id, a => a.Data);
        return attempts
            .Select(a => RecoverAttempt(a, workers.GetValueOrDefault(a.Id),
                leases.GetValueOrDefault(a.Id)))
            .ToArray();
    }

    private Attempt RecoverAttempt(Attempt attempt, WorkerRecord? worker,
        SimulationLease? lease)
    {
        if (attempt.State is not (AttemptState.Reserved or
            AttemptState.Running)) return attempt;
        if (worker != null) ValidateBinding(attempt, worker);
        var observation = worker is null
            ? ObserveSimulation(attempt, lease) ??
                new WorkerObservation(TreeCondition.Unknown, false,
                "Startup interrupted before durable process identity.")
            : inspector.Inspect(worker);
        if (observation.Tree == TreeCondition.Live &&
            observation.RepositoryIntact) return attempt;
        // An absent process is never evidence of successful implementation.
        // Interrupted retains its reservation until explicit reconciliation.
        return store.Transition(attempt.Id, attempt.Version,
            AttemptState.Interrupted, JsonSerializer.Serialize(observation));
    }

    private WorkerObservation? ObserveSimulation(Attempt attempt,
        SimulationLease? lease)
    {
        if (lease == null || evidenceRoot == null) return null;
        var task = store.Contracts(attempt.PlanId).Single(t => t.Id == attempt.TaskId);
        return task.Runtime == "simulated"
            ? lease.Inspect(evidenceRoot, attempt.Id) : null;
    }

    private static void ValidateBinding(Attempt attempt, WorkerRecord worker)
    {
        if (worker.AttemptId != attempt.Id)
            throw new InvalidDataException("Worker identity mismatch.");
    }

    public Attempt Reconcile(string planId, string attemptId,
        long measuredTokens, string usageEvidence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(measuredTokens);
        ArgumentException.ThrowIfNullOrWhiteSpace(usageEvidence);
        using var guard = store.AcquireStartupGuard();
        var attempt = store.Attempts(planId).Single(a => a.Id == attemptId);
        var worker = store.Artifacts<WorkerRecord>(planId,
            WorkerRecord.ArtifactKind).SingleOrDefault(a => a.Id == attemptId);
        WorkerObservation? observation;
        if (worker == null)
        {
            var lease = store.Artifacts<SimulationLease>(planId, "simulation")
                .SingleOrDefault(a => a.Id == attemptId);
            observation = ObserveSimulation(attempt, lease?.Data);
        }
        else
        {
            ValidateBinding(attempt, worker.Data);
            observation = inspector.Inspect(worker.Data);
        }
        if (observation == null)
            throw new InvalidOperationException("Missing worker identity.");
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
        return store.Reconcile(attempt.Id, attempt.Version, evidence,
            measuredTokens, confirmedStopped: true);
    }
}

