using Pda.Core;
using Pda.Scheduling;

namespace Pda.Application;

public sealed record ExecutionOutcome(string Plan, int Integrated,
    int TotalTasks, ExecutionStatus Status)
{
    public bool Complete => Integrated == TotalTasks && TotalTasks > 0;
}

public sealed class PlanExecution(IStateStore store, ExecutionCoordinator workers,
    Func<string, string, CancellationToken, Task<Attempt>> verifyAndIntegrate)
{
    public async Task<ExecutionOutcome> ExecuteAsync(string planId,
        int concurrency, CancellationToken cancellation = default)
    {
        if (concurrency is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        if (!store.Plans().Any(p => p.Id == planId))
            throw new KeyNotFoundException("Unknown plan.");

        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var before = workers.Status(planId);
            if (before.Paused) break;
            var ready = before.Attempts.Where(a => a.State is
                AttemptState.AwaitingVerification or AttemptState.Verified);
            foreach (var attempt in ready)
            {
                var verified = await verifyAndIntegrate(planId, attempt.Id,
                    cancellation);
                if (verified.State == attempt.State)
                    return Outcome(planId);
            }
            var batch = Enumerable.Range(0, concurrency)
                .Select(_ => workers.RunNextAsync(planId, cancellation));
            var results = await Task.WhenAll(batch);
            if (results.All(a => a == null)) break;
        }
        return Outcome(planId);
    }

    private ExecutionOutcome Outcome(string planId)
    {
        var status = workers.Status(planId);
        return new(planId, status.Attempts.Count(a =>
            a.State == AttemptState.Integrated), store.Contracts(planId).Count,
            status);
    }
}
