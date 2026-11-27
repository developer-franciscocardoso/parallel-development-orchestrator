using System.Security.Cryptography;
using System.Text;
using Pda.Core;
using Pda.Repositories;
using Pda.Verification;

namespace Pda.Quality;

public sealed partial class QualityCoordinator
{
    private async Task<Attempt> Integrate(Attempt attempt, PlanWorktree workspace,
        TaskContract task, GitCandidate candidate, CancellationToken token)
    {
        var plan = attempt.PlanId;
        var id = attempt.Id;
        var prior = store.Artifacts<RecordedHead>(plan, "integration-head")
            .SingleOrDefault(a => a.Id == plan);
        var oldHead = prior?.Data.Commit ?? workspace.Baseline.Commit;
        var merge = Load<GitMerge>(plan, "merge", id);
        if (merge == null)
        {
            if (!git.IsAncestor(workspace.Baseline.Repository,
                    candidate.Attempt.Baseline.Commit, oldHead))
                throw new InvalidDataException(
                    "Candidate base is outside the current integration history.");
            merge = Save(plan, "merge", id,
                operations.PrepareMerge(workspace, oldHead, candidate, id));
        }
        if (merge.Plan != workspace || merge.Candidate.Commit != candidate.Commit ||
            merge.Candidate.Attempt != candidate.Attempt ||
            (oldHead != merge.PreviousHead && oldHead != merge.Commit))
            throw new InvalidDataException("Integration target changed.");
        var changes = git.ChangedPaths(workspace.Baseline.Repository,
            merge.PreviousHead, merge.Commit);
        if (changes.Count == 0 || changes.Any(change =>
            new[] {change.OldPath, change.NewPath}.OfType<string>()
                .Any(path => !QualityScope.Allows(task, path))))
            throw new InvalidDataException("Combined change exceeds task scope.");
        var tasks = store.Attempts(plan)
            .Where(a => a.State == AttemptState.Integrated)
            .Select(a => a.TaskId).Append(task.Id).ToHashSet();
        foreach (var contract in store.Contracts(plan).Where(t => tasks.Contains(t.Id)))
            await Regression(attempt, contract, merge, changes, token);
        token.ThrowIfCancellationRequested();
        var intent = Load<GitMerge>(plan, "advance-intent", id);
        if (intent == null) Save(plan, "advance-intent", id, merge);
        else if (intent.Commit != merge.Commit ||
            intent.PreviousHead != merge.PreviousHead || intent.Plan != workspace)
            throw new InvalidDataException("Advance intent changed.");

        using var guard = await AcquireStartupGuard(token);
        var advanced = operations.Advance(merge);
        if (advanced.Branch != workspace.Branch || advanced.Commit != merge.Commit ||
            advanced.Repository.CommonDirectory != workspace.Baseline.Repository.CommonDirectory)
            throw new InvalidDataException("Advanced plan head differs from intent.");
        if (prior?.Data.Commit != merge.Commit)
            store.PutArtifact(plan, "integration-head", plan,
                new RecordedHead(merge.Commit), prior?.Version ?? 0);
        var integrated = new IntegratedChange(id, candidate.Attempt.Baseline.Commit,
            candidate.Commit, merge.PreviousHead, merge.Commit, advanced.Branch);
        var recorded = Load<IntegratedChange>(plan, "integration", id);
        if (recorded == null) Save(plan, "integration", id, integrated);
        else if (recorded != integrated)
            throw new InvalidDataException("Integration record is inconsistent.");
        return store.Transition(id, attempt.Version, AttemptState.Integrated,
            merge.Commit);
    }

    private async Task Regression(Attempt attempt, TaskContract task,
        GitMerge merge, IReadOnlyList<GitPathChange> changes, CancellationToken token)
    {
        var binding = CandidateBinding.For(merge.PreviousHead, merge.Commit, task);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            attempt.Id + "/" + task.Id)));
        var report = Load<CheckReport>(attempt.PlanId, "regression", key);
        if (report == null)
        {
            var input = FreshInput(attempt, task, binding, changes,
                merge.Path, merge.Plan.WorktreeRoot, "regression");
            var measured = await verification.RunAsync(input, token);
            token.ThrowIfCancellationRequested();
            report = Save(attempt.PlanId, "regression", key, measured);
        }
        RequireCheck(report, binding, task);
    }

    private async Task<IDisposable> AcquireStartupGuard(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return store.AcquireStartupGuard(); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(100, token); }
        }
    }
}
