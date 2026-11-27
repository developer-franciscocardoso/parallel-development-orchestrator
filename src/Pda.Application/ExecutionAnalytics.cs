using Pda.Core;

namespace Pda.Application;

public sealed record PlanMetrics(int Attempts, int Integrated,
    long KnownTokens, int UnknownUsageAttempts, int InterruptedAttempts,
    double? IntegratedPerMinute, double? DispatchWaitP95Ms,
    double? WorkerDurationP95Ms, double? AverageActiveWorkers,
    double? ElapsedSeconds);

public static class ExecutionAnalytics
{
    public static PlanMetrics Measure(IStateStore store, string planId)
    {
        var attempts = store.Attempts(planId);
        var journal = store.History(planId);
        var created = journal.FirstOrDefault(e => e.Kind == "PlanCreated");
        if (created == null) throw new KeyNotFoundException("Unknown plan.");
        var events = journal.Where(e => e.AttemptId != null)
            .ToLookup(e => e.AttemptId!);
        var waits = new List<double>();
        var durations = new List<double>();
        var workerTime = 0d;
        var end = journal[^1].Timestamp;
        if (attempts.Any(a => a.State is AttemptState.Running
            or AttemptState.Reserved)) end = DateTimeOffset.UtcNow;

        foreach (var attempt in attempts)
        {
            var history = events[attempt.Id].ToArray();
            var start = history.FirstOrDefault(e => e.Kind == "Running");
            var reserved = history.FirstOrDefault(e => e.Kind == "Reserved");
            if (start == null || reserved == null) continue;
            waits.Add((start.Timestamp - reserved.Timestamp).TotalMilliseconds);
            var stopped = history.FirstOrDefault(e => e.Sequence > start.Sequence);
            var duration = ((stopped?.Timestamp ?? end) - start.Timestamp)
                .TotalMilliseconds;
            if (duration < 0) continue;
            workerTime += duration;
            if (stopped != null) durations.Add(duration);
        }
        var elapsed = (end - created.Timestamp).TotalSeconds;
        var integrated = attempts.Count(a => a.State == AttemptState.Integrated);
        return new(attempts.Count, integrated, attempts.Sum(a => a.UsedTokens ?? 0),
            attempts.Count(a => a.UsedTokens == null),
            journal.Count(e => e.Kind == "Interrupted"),
            elapsed > 0 ? integrated * 60 / elapsed : null,
            Percentile(waits), Percentile(durations),
            elapsed > 0 ? workerTime / (elapsed * 1000) : null,
            elapsed >= 0 ? elapsed : null);
    }

    private static double? Percentile(List<double> samples)
    {
        if (samples.Count == 0) return null;
        samples.Sort();
        return samples[(int)Math.Ceiling(samples.Count * .95) - 1];
    }
}
