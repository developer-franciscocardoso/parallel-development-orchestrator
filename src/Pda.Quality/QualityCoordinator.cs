using System.Security.Cryptography;
using System.Text;
using Pda.Agents;
using Pda.Core;
using Pda.Repositories;
using Pda.Scheduling;
using Pda.Security;
using Pda.Verification;

namespace Pda.Quality;

public sealed partial class QualityCoordinator(IStateStore store,
    GitQualityOperations operations, IVerificationRunner verification,
    ICandidateReviewer reviewer, string evidenceRoot)
{
    private readonly GitRepositoryService git = new();

    public async Task<Attempt> ProcessAsync(string planId, string attemptId,
        CancellationToken token = default)
    {
        ContractValidator.ValidateId(planId);
        ContractValidator.ValidateId(attemptId);
        var root = TrustedPaths.Directory(evidenceRoot);
        TrustedPaths.OutsideRepository(root);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(planId));
        var lockPath = Path.Combine(root, "quality-" +
            Convert.ToHexStringLower(key) + ".lock");
        using var gate = await Lock(lockPath, token);
        var attempt = store.Attempts(planId).Single(a => a.Id == attemptId);
        if (attempt.State is AttemptState.Integrated or AttemptState.Blocked)
            return attempt;
        if (attempt.State is not (AttemptState.AwaitingVerification or
            AttemptState.Verified))
            throw new InvalidOperationException("Attempt is not ready for quality.");
        var unfinished = store.Artifacts<GitMerge>(planId, "advance-intent");
        var active = store.Attempts(planId).Where(a =>
            a.State == AttemptState.Verified && a.Id != attemptId);
        if (active.Any(a => unfinished.Any(p => p.Id == a.Id)))
            throw new InvalidOperationException(
                "Reconcile the prior integration intent before processing another.");
        var stage = "candidate";
        try
        {
            var task = store.Contracts(planId).Single(t => t.Id == attempt.TaskId);
            var plan = store.Plans().Single(p => p.Id == planId);
            var workspace = Required<PlanWorktree>(planId, "workspace", planId);
            var dispatch = Required<DispatchRecord>(planId, "dispatch", attemptId);
            var result = Required<RuntimeResult>(planId, "result", attemptId);
            if (workspace.PlanId != plan.Id || plan.BaselineCommit == null ||
                workspace.Baseline.Commit != plan.BaselineCommit ||
                dispatch.Worktree.Baseline.Repository.CommonDirectory !=
                workspace.Baseline.Repository.CommonDirectory ||
                dispatch.Worktree.Baseline.Branch != workspace.Branch ||
                dispatch.Worktree.Baseline.Repository.Root != workspace.Path ||
                result.Error != RuntimeError.None ||
                result.Report?.Status != "completed" ||
                result.Report.TaskId != task.Id ||
                result.Report.AttemptId != attemptId ||
                attempt.UsedTokens == null || result.Usage?.Total != attempt.UsedTokens)
                throw new InvalidDataException("Dispatch evidence is inconsistent.");
            var candidate = Load<GitCandidate>(planId, "candidate", attemptId)
                ?? Save(planId, "candidate", attemptId, operations.Capture(
                    dispatch.Worktree, result.Report.Commit,
                    path => QualityScope.Allows(task, path)));
            ValidateCandidate(candidate, dispatch, result.Report, task);
            var binding = CandidateBinding.For(dispatch.Worktree.Baseline.Commit,
                candidate.Commit, task);
            stage = "verification";
            await Verify(attempt, task, candidate, binding, token);
            if (attempt.State == AttemptState.AwaitingVerification)
                attempt = store.Transition(attemptId, attempt.Version,
                    AttemptState.Verified, binding.ContractDigest);
            stage = "integration";
            return await Integrate(attempt, workspace, task, candidate, token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Once a ref movement was intended its effect may need recovery.
            // Retain Verified so a restart can compare the two expected heads.
            var pending = Load<GitMerge>(planId, "advance-intent", attemptId);
            var fix = pending == null
                ? "Inspect the retained checkout and evidence before replanning."
                : "Reconcile the recorded old and new plan heads before retry.";
            var prior = store.Artifacts<QualityFailure>(planId, "quality-failure")
                .SingleOrDefault(e => e.Id == attemptId);
            store.PutArtifact(planId, "quality-failure", attemptId,
                new QualityFailure(stage, error.Message, fix), prior?.Version ?? 0);
            if (pending != null) throw;
            return store.Transition(attemptId, attempt.Version, AttemptState.Blocked,
                stage + ": " + error.Message);
        }
    }

    private void ValidateCandidate(GitCandidate candidate, DispatchRecord dispatch,
        WorkerReport report, TaskContract task)
    {
        if (candidate.Attempt != dispatch.Worktree || candidate.Changes.Count == 0 ||
            report.Commit.Length != 0 && report.Commit != candidate.Commit)
            throw new InvalidDataException("Candidate does not match its attempt.");
        var actual = git.ChangedPaths(candidate.Attempt.Baseline.Repository,
            candidate.Attempt.Baseline.Commit, candidate.Commit);
        if (!actual.SequenceEqual(candidate.Changes) || actual.Any(change =>
            new[] {change.OldPath, change.NewPath}.OfType<string>()
                .Any(path => !QualityScope.Allows(task, path))))
            throw new InvalidDataException("Candidate scope changed.");
    }

    private async Task Verify(Attempt attempt, TaskContract task,
        GitCandidate candidate, CandidateBinding binding, CancellationToken token)
    {
        var plan = attempt.PlanId;
        var id = attempt.Id;
        var check = Load<CheckReport>(plan, "check", id);
        if (check == null)
        {
            var input = FreshInput(attempt, task, binding, candidate.Changes,
                candidate.Attempt.Path, candidate.Attempt.WorktreeRoot, "check");
            var measured = await verification.RunAsync(input, token);
            token.ThrowIfCancellationRequested();
            check = Save(plan, "check", id, measured);
        }
        RequireCheck(check, binding, task);
        var review = Load<ReviewReport>(plan, "review", id);
        if (review == null)
        {
            var input = FreshInput(attempt, task, binding, candidate.Changes,
                candidate.Attempt.Path, candidate.Attempt.WorktreeRoot, "review");
            var findings = await reviewer.ReviewAsync(input, token);
            token.ThrowIfCancellationRequested();
            review = Save(plan, "review", id, findings);
        }
        if (review.Binding != binding || !review.Approved ||
            string.IsNullOrWhiteSpace(review.Reviewer) || review.Findings.Any(f =>
                f.Severity.Equals("blocking", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Independent candidate review rejected.");
    }

    private VerificationInput FreshInput(Attempt attempt, TaskContract task,
        CandidateBinding binding, IReadOnlyList<GitPathChange> changes,
        string source, string trees, string stage)
    {
        var baseline = git.Register(source);
        if (baseline.Commit != binding.CandidateCommit)
            throw new InvalidDataException("Verification source HEAD changed.");
        var key = Guid.NewGuid().ToString("N");
        var scratch = git.PlanAttempt(baseline, trees, stage, key);
        var output = Path.Combine(TrustedPaths.Directory(evidenceRoot), key);
        if (Path.Exists(output))
            throw new IOException("Verification evidence destination exists.");
        Save(attempt.PlanId, "quality-worktree", key,
            new { attempt.Id, task = task.Id, binding, scratch, output });
        Directory.CreateDirectory(output);
        TrustedPaths.Directory(output);
        git.CreateAttempt(scratch);
        return new(binding, task, scratch.Path, output, changes);
    }

    private void RequireCheck(CheckReport report,
        CandidateBinding binding, TaskContract task)
    {
        if (report.Binding != binding || !report.Passed ||
            report.Checks.Count != task.Verification.Length)
            throw new InvalidDataException("Independent checks rejected: " +
                report.Detail);
        for (var i = 0; i < report.Checks.Count; i++)
        {
            var check = report.Checks[i];
            var requested = task.Verification[i];
            if (check.Command.Executable != requested.Executable ||
                !check.Command.Arguments.SequenceEqual(requested.Arguments) ||
                check.ExitCode != 0 || check.Truncated || check.TimedOut ||
                check.Cancelled)
                throw new InvalidDataException("Command evidence is inconsistent.");
            VerifyLog(check.OutputLog, check.OutputSha256);
            VerifyLog(check.ErrorLog, check.ErrorSha256);
        }
    }

    private void VerifyLog(string path, string expected)
    {
        var root = TrustedPaths.Directory(evidenceRoot);
        var file = TrustedPaths.File(path);
        var relative = Path.GetRelativePath(root, file);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new InvalidDataException("Command log outside evidence root.");
        using var stream = File.OpenRead(file);
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        if (!digest.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Command evidence hash mismatch.");
    }

    private static async Task<FileStream> Lock(string path,
        CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (Path.Exists(path)) TrustedPaths.File(path);
                return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(100, token); }
        }
    }

    private T? Load<T>(string plan, string kind, string id) where T : class =>
        store.Artifacts<T>(plan, kind).SingleOrDefault(a => a.Id == id)?.Data;

    private T Required<T>(string plan, string kind, string id) where T : class =>
        Load<T>(plan, kind, id) ?? throw new InvalidDataException(
            "Missing " + kind + " evidence.");

    private T Save<T>(string plan, string kind, string id, T value) =>
        store.PutArtifact(plan, kind, id, value).Data;
}
