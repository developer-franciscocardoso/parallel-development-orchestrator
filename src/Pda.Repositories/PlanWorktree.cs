using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pda.Repositories;

public sealed record PlanWorktree(RepositoryBaseline Baseline,
    string WorktreeRoot, string PlanId, string Path, string Branch);

public sealed partial class GitRepositoryService
{
    public PlanWorktree PlanWorkspace(RepositoryBaseline baseline,
        string worktreeRoot, string planId)
    {
        var plan = DescribePlan(baseline, worktreeRoot, planId);
        var registrations = ListWorktrees(baseline.Repository);
        CheckPlanLocation(plan, registrations);
        RequireAvailablePlan(plan, registrations);
        return plan;
    }

    public RepositoryBaseline EnsureWorkspace(PlanWorktree workspace,
        string expectedHead)
    {
        var source = workspace.Baseline;
        var canonical = DescribePlan(source, workspace.WorktreeRoot,
            workspace.PlanId);
        if (canonical != workspace)
            throw new GitRepositoryException("Unexpected plan identity.");

        var repository = source.Repository;
        var head = Commit(repository.Root, expectedHead);
        if (!IsAncestor(repository, source.Commit, head))
            throw new GitRepositoryException("Plan head has an unrelated base.");

        var registrations = ListWorktrees(repository);
        CheckPlanLocation(workspace, registrations);
        if (!registrations.Any(r => LocalPaths.Equal(r.Path, workspace.Path)))
        {
            RequireAvailablePlan(workspace, registrations);
            if (head != source.Commit || Register(repository.Root) != source)
                throw new GitRepositoryException("Plan source baseline changed.");

            git.Require(repository.Root, "worktree", "add", "--no-track", "-b",
                workspace.Branch, "--", workspace.Path, source.Commit);
            registrations = ListWorktrees(repository);
        }

        var entry = registrations.SingleOrDefault(r =>
            LocalPaths.Equal(r.Path, workspace.Path));
        if (entry is null || entry.Branch != workspace.Branch ||
            entry.Commit != head || entry.IsLocked || entry.IsPrunable)
            throw new GitRepositoryException("Unexpected plan registration.");

        var current = Register(workspace.Path);
        if (current.Repository.CommonDirectory != repository.CommonDirectory ||
            current.Branch != workspace.Branch || current.Commit != head)
            throw new GitRepositoryException("Plan checkout changed.");
        return current;
    }

    private PlanWorktree DescribePlan(RepositoryBaseline source, string root,
        string planId)
    {
        if (planId is null || !Regex.IsMatch(planId,
            @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z"))
            throw new GitRepositoryException("Invalid plan ID.");
        var repo = Verify(source.Repository);
        Commit(repo.Root, source.Commit);
        var storage = LocalPaths.DirectoryPath(root);
        LocalPaths.RequireOutsideCheckout(storage);
        if (LocalPaths.Overlaps(storage, repo.CommonDirectory))
            throw new GitRepositoryException("Unsafe plan storage location.");

        // Git refs and Windows filesystems have different naming restrictions.
        var key = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(planId)));
        return new(source, storage, planId, Path.Combine(storage, "plan-" + key),
            "pda/plan/" + key);
    }

    private static void CheckPlanLocation(PlanWorktree plan,
        IReadOnlyList<WorktreeRegistration> registrations)
    {
        var source = plan.Baseline.Repository;
        LocalPaths.RejectReparsePoints(plan.Path);
        if (LocalPaths.Overlaps(source.Root, plan.Path) ||
            registrations.Any(r => LocalPaths.Contains(r.Path, plan.WorktreeRoot)
                || !LocalPaths.Equal(r.Path, plan.Path) &&
                LocalPaths.Overlaps(r.Path, plan.Path)))
            throw new GitRepositoryException("Plan overlaps another checkout.");
    }

    private void RequireAvailablePlan(PlanWorktree plan,
        IReadOnlyList<WorktreeRegistration> registrations)
    {
        LocalPaths.RequireAbsent(plan.Path);
        if (registrations.Any(r => LocalPaths.Equal(r.Path, plan.Path) ||
            r.Branch == plan.Branch))
            throw new GitRepositoryException("Plan workspace already registered.");
        var reference = git.Run(plan.Baseline.Repository.Root, "show-ref",
            "--verify", "--quiet", "refs/heads/" + plan.Branch);
        if (reference.ExitCode != 1)
            throw new GitRepositoryException("Plan branch is unavailable.");
    }
}
