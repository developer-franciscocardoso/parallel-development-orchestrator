using System.Security.Cryptography;
using System.Text;

namespace Pda.Repositories;

public sealed partial class GitRepositoryService
{
    public GitMerge PrepareMerge(PlanWorktree plan, string expectedHead,
        GitCandidate candidate, string mergeId)
    {
        if (string.IsNullOrWhiteSpace(mergeId) || mergeId.Length > 256)
            throw new GitRepositoryException("Invalid integration identity.");
        var target = EnsureWorkspace(plan, expectedHead);
        CheckVerifiedWorker(plan, candidate);
        if (!IsAncestor(target.Repository, candidate.Attempt.Baseline.Commit,
                expectedHead) ||
            IsAncestor(target.Repository, candidate.Commit, expectedHead))
            throw new GitRepositoryException("Incompatible merge base.");

        var identity = string.Join('\n', plan.Branch, expectedHead,
            candidate.Commit, mergeId);
        var key = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var branch = "pda/merge/" + key;
        var path = Path.Combine(plan.WorktreeRoot, "merge-" + key);
        LocalPaths.RejectReparsePoints(path);
        if (!Directory.Exists(path))
        {
            LocalPaths.RequireAbsent(path);
            git.Require(plan.Path, "worktree", "add", "--no-track", "-b",
                branch, "--", path, expectedHead);
        }

        var state = RegisteredCheckout(target.Repository, path, branch);
        if (!state.IsClean)
            throw new GitRepositoryException("Preserved dirty merge checkout.");
        var head = state.Commit;
        if (head == expectedHead)
        {
            try
            {
                CoordinatorGit(path, "merge", "--no-edit", "--no-ff",
                    "--no-autostash", "--no-verify-signatures", "--no-gpg-sign",
                    "-s", "ort", "-m", "PDA verified candidate", "--",
                    candidate.Commit);
            }
            catch (GitRepositoryException error)
            {
                throw new GitRepositoryException(
                    $"Merge failed. Preserved {branch} in {path}.", error);
            }
            head = Register(path).Commit;
        }
        var merge = new GitMerge(plan, candidate, expectedHead,
            head, path, branch);
        CheckMerge(merge);
        return merge;
    }

    public RepositoryBaseline AdvancePlan(GitMerge merge)
    {
        var plan = merge.Plan;
        var source = plan.Baseline;
        if (DescribePlan(source, plan.WorktreeRoot, plan.PlanId) != plan)
            throw new GitRepositoryException("Unexpected plan identity.");
        CheckVerifiedWorker(plan, merge.Candidate);
        CheckMerge(merge);
        if (!IsAncestor(source.Repository, source.Commit, merge.PreviousHead) ||
            !IsAncestor(source.Repository,
                merge.Candidate.Attempt.Baseline.Commit, merge.PreviousHead))
            throw new GitRepositoryException("Unexpected plan ancestry.");

        var state = RegisteredCheckout(source.Repository, plan.Path,
            plan.Branch);
        if (state.Commit == merge.Commit)
            return EnsureWorkspace(plan, merge.Commit);
        if (state.Commit != merge.PreviousHead)
            throw new GitRepositoryException("Plan branch advanced.");
        StageIntegration(plan.Path, merge);
        git.Require(plan.Path, "update-ref", "refs/heads/" + plan.Branch,
            merge.Commit, merge.PreviousHead);
        return EnsureWorkspace(plan, merge.Commit);
    }

    private void StageIntegration(string path, GitMerge merge)
    {
        RejectUnsafeIndex(path);
        if (git.Require(path, "ls-files", "--others", "-z").Length != 0 ||
            git.Run(path, "diff", "--quiet", "--no-ext-diff",
                "--no-textconv", "--").ExitCode != 0)
            throw new GitRepositoryException("Plan contains local edits.");

        var current = Line(git.Require(path, "write-tree"));
        var result = TreeId(path, merge.Commit);
        // A crash after read-tree may leave the verified result staged.
        if (current == result)
            return;
        if (current != TreeId(path, merge.PreviousHead))
            throw new GitRepositoryException("Plan index changed.");
        git.Require(path, "read-tree", "-u", "-m", merge.PreviousHead,
            merge.Commit);
        if (Line(git.Require(path, "write-tree")) != result)
            throw new GitRepositoryException("Could not stage merged tree.");
    }

    private string TreeId(string root, string commit) =>
        Line(git.Require(root, "rev-parse", "--verify", "--end-of-options",
            commit + "^{tree}"));

    private void CheckVerifiedWorker(PlanWorktree plan, GitCandidate candidate)
    {
        var source = candidate.Attempt.Baseline;
        if (source.Branch != plan.Branch ||
            source.Repository.Root != plan.Path ||
            source.Repository.CommonDirectory !=
                plan.Baseline.Repository.CommonDirectory)
            throw new GitRepositoryException("Candidate plan mismatch.");
        var state = OpenWorker(candidate.Attempt);
        if (!state.IsClean || state.Commit != candidate.Commit ||
            Parents(state.Repository.Root, candidate.Commit) != source.Commit)
            throw new GitRepositoryException("Verified candidate changed.");
    }

    private void CheckMerge(GitMerge merge)
    {
        const string prefix = "pda/merge/";
        if (!merge.Branch.StartsWith(prefix, StringComparison.Ordinal))
            throw new GitRepositoryException("Invalid merge branch.");
        var key = merge.Branch[prefix.Length..];
        if (key.Length != 64 || !key.All(Uri.IsHexDigit) ||
            merge.Path != Path.Combine(merge.Plan.WorktreeRoot, "merge-" + key))
            throw new GitRepositoryException("Invalid merge workspace.");
        var state = RegisteredCheckout(merge.Plan.Baseline.Repository,
            merge.Path, merge.Branch);
        var tree = Line(git.Require(merge.Path, "merge-tree", "--write-tree",
            "--no-messages", merge.PreviousHead, merge.Candidate.Commit));
        if (!state.IsClean || state.Commit != merge.Commit ||
            TreeId(merge.Path, merge.Commit) != tree ||
            Parents(merge.Path, merge.Commit) !=
                merge.PreviousHead + " " + merge.Candidate.Commit)
            throw new GitRepositoryException("Combined merge commit changed.");
    }
}
