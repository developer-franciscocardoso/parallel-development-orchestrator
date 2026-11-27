using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pda.Agents;
using Pda.Core;
using Pda.Execution;
using Pda.Recovery;
using Pda.Repositories;
using Pda.Security;

namespace Pda.Scheduling;

public sealed partial class ExecutionCoordinator(IStateStore store,
    Func<string, IAgentRuntime> resolveRuntime,
    Func<string, DispatchAssignment?> reserve,
    Func<PlanInfo, RepositoryBaseline> planBaseline,
    string worktreeRoot, string evidenceRoot)
{
    private readonly GitRepositoryService git = new();

    public async Task<Attempt?> RunNextAsync(string planId,
        CancellationToken token = default)
    {
        IDisposable? guard = await AcquireGuard(token);
        FileStream? lease = null;
        Attempt? attempt = null;
        var runtimeEntered = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (IsPaused(planId)) return null;
            var assignment = reserve(planId);
            if (assignment == null) return null;
            attempt = assignment.Attempt;
            var task = assignment.Task;
            if (attempt.PlanId != planId || attempt.TaskId != task.Id ||
                attempt.State != AttemptState.Reserved)
                throw new InvalidOperationException("Invalid dispatch assignment.");
            var runtime = resolveRuntime(task.Runtime);
            if (runtime.Id != task.Runtime)
                throw new InvalidOperationException("Runtime assignment mismatch.");
            var simulated = runtime is SimulatedRuntime;
            var record = Prepare(assignment);
            var request = new AgentRequest(task.Id, attempt.Id,
                record.Worktree.Path, record.OutputPath, record.ErrorPath,
                record.ReportPath, WorkerPrompt(task),
                TimeSpan.FromSeconds(task.TimeoutSeconds), assignment.Model);

            void Running()
            {
                attempt = store.Transition(attempt.Id, attempt.Version,
                    AttemptState.Running, simulated
                        ? "Simulated runtime lease acquired."
                        : "Supervised worker identity recorded.");
                Interlocked.Exchange(ref guard, null)?.Dispose();
            }

            Task Started(SupervisedIdentity identity, CancellationToken stop)
            {
                stop.ThrowIfCancellationRequested();
                var tree = record.Worktree;
                var worker = new WorkerRecord(attempt.Id, tree.Path,
                    tree.Branch, tree.Baseline.Commit, identity.ProcessId,
                    identity.StartedUtc, identity.JobName, record.OutputPath,
                    record.ErrorPath, tree.Baseline.Repository.CommonDirectory);
                store.PutArtifact(planId, WorkerRecord.ArtifactKind,
                    attempt.Id, worker);
                Running();
                return Task.CompletedTask;
            }

            if (simulated)
            {
                var path = SimulationLease.FilePath(TrustedPaths.Directory(evidenceRoot),
                    attempt.Id);
                lease = new(path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None);
                store.PutArtifact(planId, "simulation", attempt.Id,
                    new SimulationLease(path));
                Running();
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var monitoring = new CancellationTokenSource();
            var monitor = WatchCancel(planId, attempt.Id, stop, monitoring.Token);
            RuntimeResult result;
            try
            {
                runtimeEntered = true;
                result = await runtime.RunAsync(request,
                    new(Started, _ => { }), stop.Token);
            }
            finally
            {
                await monitoring.CancelAsync();
                await monitor;
            }
            if (result.PreflightRejected)
            {
                if (runtime is not CodexRuntime || result.Simulated ||
                    result.Process != null || result.Report != null ||
                    result.Usage != new TokenUsage(0, 0, 0) ||
                    result.Error is not (RuntimeError.Authentication or
                        RuntimeError.Unavailable or RuntimeError.PolicyDenied))
                    throw new InvalidDataException("Inconsistent preflight evidence.");
                return store.RejectPreflight(attempt.Id, attempt.Version, result,
                    result.Detail, result.Error == RuntimeError.PolicyDenied);
            }
            store.PutArtifact(planId, "result", attempt.Id, result);
            return Finish(attempt, result, simulated,
                stop.IsCancellationRequested || CancellationRequested(planId,
                    attempt.Id));
        }
        catch (Exception e) when (attempt != null)
        {
            return store.Transition(attempt.Id, attempt.Version,
                runtimeEntered ? AttemptState.Interrupted : AttemptState.Failed,
                $"{e.GetType().Name}: {e.Message}",
                runtimeEntered ? null : 0L);
        }
        finally
        {
            lease?.Dispose();
            guard?.Dispose();
        }
    }

    private DispatchRecord Prepare(DispatchAssignment assignment)
    {
        var attempt = assignment.Attempt;
        var plan = store.Plans().Single(p => p.Id == attempt.PlanId);
        var baseline = planBaseline(plan);
        var logs = TrustedPaths.Directory(evidenceRoot);
        var trees = TrustedPaths.Directory(worktreeRoot);
        TrustedPaths.OutsideRepository(logs);
        TrustedPaths.OutsideRepository(trees);
        TrustedPaths.Disjoint(logs, trees);
        var tree = git.PlanAttempt(baseline, trees,
            TaskSegment(assignment.Task.Id), attempt.Id);
        var folder = Path.Combine(logs, attempt.Id);
        var record = new DispatchRecord(tree, Path.Combine(folder, "stdout.jsonl"),
            Path.Combine(folder, "stderr.log"), Path.Combine(folder, "report.json"),
            assignment.Task.Runtime, assignment.Model);
        store.PutArtifact(attempt.PlanId, "dispatch", attempt.Id, record);
        if (Path.Exists(folder))
            throw new IOException("Attempt evidence directory already exists.");
        Directory.CreateDirectory(folder);
        TrustedPaths.Directory(folder);
        git.CreateAttempt(tree);
        return record;
    }

    private static string WorkerPrompt(TaskContract task) =>
        "Work only within the task contract. Leave only deliverable edits. " +
        "Keep temporary build output out of the submitted worktree. " +
        "Return completed edits even if the commit field is empty. " +
        "The coordinator inspects, commits, and verifies the actual changes.\n" +
        JsonSerializer.Serialize(task);

    private static string TaskSegment(string id)
    {
        if (id.Length <= 48 && id.All(c => char.IsAsciiLetterOrDigit(c) ||
                c is '-' or '_')) return id;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexStringLower(digest)[..48];
    }

    private async Task<IDisposable> AcquireGuard(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return store.AcquireStartupGuard(); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(100, token); }
        }
    }

    private async Task WatchCancel(string plan, string attempt,
        CancellationTokenSource stop, CancellationToken end)
    {
        try
        {
            while (!CancellationRequested(plan, attempt))
                await Task.Delay(250, end);
            await stop.CancelAsync();
        }
        catch (OperationCanceledException) when (end.IsCancellationRequested) { }
        catch
        {
            await stop.CancelAsync();
            throw;
        }
    }

    private Attempt Finish(Attempt attempt, RuntimeResult result,
        bool simulated, bool cancelled)
    {
        var usage = result.Usage?.Total;
        var state = result switch
        {
            _ when result.Simulated != simulated => AttemptState.Interrupted,
            _ when !simulated && attempt.State == AttemptState.Running &&
                result.Process?.TreeStopped != true => AttemptState.Interrupted,
            _ when cancelled || result.Error == RuntimeError.Cancelled =>
                AttemptState.Cancelled,
            { Error: RuntimeError.PolicyDenied or RuntimeError.Protocol } =>
                AttemptState.Blocked,
            { Error: not RuntimeError.None } => AttemptState.Failed,
            _ when usage == null => AttemptState.Blocked,
            _ when result.Report?.TaskId != attempt.TaskId ||
                result.Report.AttemptId != attempt.Id ||
                result.Report.Status != "completed" ||
                attempt.State != AttemptState.Running => AttemptState.Blocked,
            _ => AttemptState.AwaitingVerification
        };
        return store.Transition(attempt.Id, attempt.Version, state,
            result.Detail, usage);
    }
}


