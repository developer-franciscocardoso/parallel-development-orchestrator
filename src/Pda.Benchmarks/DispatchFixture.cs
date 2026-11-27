using System.Diagnostics;
using Pda.Agents;
using Pda.Application;
using Pda.Core;
using Pda.Persistence;
using Pda.Planning;
using Pda.Providers;
using Pda.Quality;
using Pda.Repositories;
using Pda.Verification;

namespace Pda.Benchmarks;

internal sealed partial class BenchFixture
{
    private async Task Dispatch(StateStore store, PlanningService planner,
        ProviderRegistry providers, Measurements data, bool integrate,
        CancellationToken token)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0, active = 0, peak = 0;
        async Task<WorkerReport> Implement(AgentRequest request, CancellationToken stop)
        {
            if (request.TaskId != "single")
            {
                int count = Interlocked.Increment(ref active);
                if (count == 2) Interlocked.Exchange(ref peak, 2);
                if (Interlocked.Increment(ref entered) == 2) reached.TrySetResult();
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(120), stop);
            }
            var path = Path.Combine(request.Worktree, request.TaskId + ".txt");
            await File.WriteAllTextAsync(path, "updated", stop);
            if (request.TaskId != "single") Interlocked.Decrement(ref active);
            return new(request.TaskId, request.AttemptId, "completed",
                "Synthetic benchmark edit.", "", [request.TaskId], [], []);
        }
        var runtime = new SimulatedRuntime(SimulationScenario.Success, Implement);
        var workers = ExecutionServices.Create(store, providers, [runtime],
            _ => "fixture", Trees, Logs, 2, Capacity);
        planner.Import("fixture", "single", 100, [Contract("single")]);
        var start = Stopwatch.GetTimestamp();
        var single = await workers.RunNextAsync("single", token);
        RequireReady(single);
        data.Elapsed("task.dispatch", start);
        if (!integrate || !await Integration(store, single!, data, token))
            store.Transition(single!.Id, single.Version, AttemptState.Cancelled,
                "Single-dispatch measurement complete.", single.UsedTokens);

        planner.Import("fixture", "parallel", 100, [Contract("left"), Contract("right")]);
        start = Stopwatch.GetTimestamp();
        var attempts = await Task.WhenAll(
            Task.Run(() => workers.RunNextAsync("parallel", token), token),
            Task.Run(() => workers.RunNextAsync("parallel", token), token));
        var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        foreach (var item in attempts) RequireReady(item);
        data.Record("parallel.throughput", attempts.Length / seconds);
        data.Record("parallel.peakWorkers", peak);
        data.Record("invariant.duplicateAssignments", attempts.Length -
            attempts.Select(a => a!.TaskId).Distinct().Count());
        if (peak != 2) throw new InvalidDataException("Workers did not overlap.");
    }

    private static void RequireReady(Attempt? attempt)
    {
        if (attempt?.State != AttemptState.AwaitingVerification ||
            attempt.UsedTokens != 13)
            throw new InvalidDataException("Synthetic worker failed to produce evidence.");
    }

    private async Task<bool> Integration(StateStore store, Attempt attempt,
        Measurements data, CancellationToken token)
    {
        var exe = Environment.GetEnvironmentVariable("PDA_CODEX_EXE") ??
            (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(p => Path.Combine(p, "codex.exe")).FirstOrDefault(File.Exists);
        if (!OperatingSystem.IsWindows() || !File.Exists(exe))
        {
            foreach (var metric in new[] {"integration.qualityAndMerge",
                "integration.prepare", "integration.advance",
                "invariant.unverifiedIntegrations"})
                data.Missing(metric, "Requires native Codex MXC on Windows.");
            return false;
        }
        GitMerge Prepare(PlanWorktree plan, string head, GitCandidate candidate,
            string id)
        {
            var t = Stopwatch.GetTimestamp();
            var merge = git.PrepareMerge(plan, head, candidate, id);
            data.Elapsed("integration.prepare", t);
            return merge;
        }
        RepositoryBaseline Advance(GitMerge merge)
        {
            var t = Stopwatch.GetTimestamp();
            var head = git.AdvancePlan(merge);
            data.Elapsed("integration.advance", t);
            return head;
        }

        var quality = new QualityCoordinator(store,
            new(git.CaptureCandidate, Prepare, Advance),
            new SandboxedVerificationRunner(new(exe, Folder("codex-home"))),
            new MechanicalReviewer(), Logs);
        var start = Stopwatch.GetTimestamp();
        var final = await quality.ProcessAsync("single", attempt.Id, token);
        token.ThrowIfCancellationRequested();
        if (final.State == AttemptState.Blocked)
        {
            var failure = store.Artifacts<QualityFailure>("single", "quality-failure")
                .Single(a => a.Id == final.Id).Data;
            foreach (var key in new[] {"integration.qualityAndMerge",
                "integration.prepare", "integration.advance",
                "invariant.unverifiedIntegrations"})
                data.Missing(key, failure.Stage + ": " + failure.Detail);
            return true;
        }
        if (final.State != AttemptState.Integrated)
            throw new InvalidDataException("Integration failed to reach a final state.");
        data.Elapsed("integration.qualityAndMerge", start);
        var check = store.Artifacts<CheckReport>("single", "check").Single().Data;
        var review = store.Artifacts<ReviewReport>("single", "review").Single().Data;
        var result = store.Artifacts<IntegratedChange>("single", "integration").Single().Data;
        var regression = store.Artifacts<CheckReport>("single", "regression").Single().Data;
        data.Record("invariant.unverifiedIntegrations",
            check.Passed && review.Approved && check.Binding == review.Binding &&
            check.Binding.CandidateCommit == result.CandidateCommit &&
            regression.Passed && regression.Binding.CandidateCommit == result.Commit &&
            regression.Binding.BaseCommit == result.PreviousHead &&
            regression.Binding.ContractDigest == review.Binding.ContractDigest ? 0 : 1);
        return true;
    }
}
