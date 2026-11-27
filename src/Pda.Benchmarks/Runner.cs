using System.Diagnostics;
using System.Runtime.InteropServices;
using Pda.Admission;
using Pda.Execution;

namespace Pda.Benchmarks;

public static class Runner
{
    public static async Task<BenchmarkReport> RunAsync(int iterations,
        CancellationToken token = default)
    {
        if (iterations is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(iterations));
        token.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var environment = new BenchmarkEnvironment(RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount);
        var data = Define();
        for (var i = 0; i < iterations; i++)
        {
            token.ThrowIfCancellationRequested();
            using var fixture = new BenchFixture();
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var begin = Stopwatch.GetTimestamp();
            await fixture.RunAsync(data, i == 0, token);
            process.Refresh();
            var consumed = process.TotalProcessorTime - cpu;
            var elapsed = Stopwatch.GetElapsedTime(begin);
            data.Record("process.cpu", 100 * consumed.TotalSeconds /
                (elapsed.TotalSeconds * Environment.ProcessorCount));
            data.Record("process.memory", process.WorkingSet64 / 1048576d);
        }
        return new(started, DateTimeOffset.UtcNow, iterations, environment,
            data.Finish());
    }

    private static Measurements Define()
    {
        var d = new Measurements();
        d.Define("database.transition", "ms", "SQLite WAL Reserved to Running", 50);
        d.Define("scheduler.decision", "ms",
            "Admission with a deterministic two-worker host capacity", 250);
        d.Define("git.worktree", "ms", "Create and inspect a linked Git worktree");
        d.Define("process.supervision", "ms", "Launch and reap native where.exe help in a job");
        d.Define("task.dispatch", "ms", "Single simulated task from reserve to report");
        d.Define("recovery", "ms", "Recover one interrupted reservation from disk");
        d.Define("parallel.throughput", "tasks/s",
            "Two concurrent simulated workers, including isolated worktree setup");
        d.Define("parallel.peakWorkers", "workers", "Overlapping simulator callbacks");
        d.Define("process.cpu", "%", "Harness CPU time / wall time / logical CPUs");
        d.Define("process.memory", "MiB", "Harness working set at workload end");
        d.Define("host.cpu", "%", "PDH system CPU sample");
        d.Define("host.memoryAvailable", "MiB", "PDH available physical memory");
        d.Define("integration.qualityAndMerge", "ms",
            "One end-to-end candidate with native MXC checks and Git merge");
        d.Define("integration.prepare", "ms", "Verified candidate merge preparation");
        d.Define("integration.advance", "ms", "Advance plan branch to checked merge");
        d.Define("invariant.lostEvents", "events", "Compare journal before/after reopen");
        d.Define("invariant.duplicateAssignments", "attempts",
            "Duplicate task assignments in two-worker fixture");
        d.Define("invariant.unverifiedIntegrations", "attempts",
            "Integrated attempts missing candidate or merged regression evidence");
        return d;
    }
}
