using Pda.Agents;
using Pda.Core;
using Pda.Repositories;

namespace Pda.Scheduling;

public sealed record DispatchAssignment(TaskContract Task, Attempt Attempt,
    string? Model);

public sealed record DispatchRecord(AttemptWorktree Worktree,
    string OutputPath, string ErrorPath, string ReportPath,
    string Runtime, string? Model);

public sealed record ExecutionStatus(bool Paused,
    IReadOnlyList<Attempt> Attempts);

public sealed partial class ExecutionCoordinator
{
    public ExecutionStatus Status(string planId) =>
        new(IsPaused(planId), store.Attempts(planId));

    public void Pause(string planId) => SetPaused(planId, true);
    public void Resume(string planId) => SetPaused(planId, false);

    public void Cancel(string planId, string attemptId)
    {
        var attempt = store.Attempts(planId).Single(a => a.Id == attemptId);
        if (CancellationRequested(planId, attemptId)) return;
        if (attempt.State is not (AttemptState.Reserved or AttemptState.Running))
            throw new InvalidOperationException("Attempt is no longer active.");
        store.PutArtifact(planId, "cancel", attemptId, true);
    }

    private bool CancellationRequested(string planId, string attempt) =>
        store.Artifacts<bool>(planId, "cancel").Any(a => a.Id == attempt);

    private bool IsPaused(string planId) => store.Artifacts<bool>(planId,
        "control").SingleOrDefault(a => a.Id == "paused")?.Data == true;

    private void SetPaused(string planId, bool paused)
    {
        if (!store.Plans().Any(plan => plan.Id == planId))
            throw new KeyNotFoundException("Unknown execution plan.");
        var prior = store.Artifacts<bool>(planId, "control")
            .SingleOrDefault(a => a.Id == "paused");
        if ((prior?.Data ?? false) != paused)
            store.PutArtifact(planId, "control", "paused", paused,
                prior?.Version ?? 0);
    }
}
