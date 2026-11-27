using Pda.Repositories;

namespace Pda.Repositories.Tests;

public sealed partial class GitRepositoryTests
{
    [Fact]
    public void CapturesScopedEditsAndAdvancesOnlyThePlanBranch()
    {
        var original = service.Register(Source);
        var plan = service.PlanWorkspace(original, Worktrees, "plan");
        var start = service.EnsureWorkspace(plan, original.Commit);
        var worker = service.PlanAttempt(start, Worktrees, "task", "first");
        service.CreateAttempt(worker);
        Assert.Throws<GitRepositoryException>(() =>
            service.CaptureCandidate(worker, null, _ => true));
        File.WriteAllText(Path.Combine(worker.Path, "code.txt"), "verified");
        var candidate = service.CaptureCandidate(worker, null,
            path => path == "code.txt");
        Assert.Equal(candidate.Commit,
            service.CaptureCandidate(worker, "", _ => true).Commit);
        Assert.Throws<GitRepositoryException>(() =>
            service.CaptureCandidate(worker, start.Commit, _ => true));
        var merged = service.PrepareMerge(plan, start.Commit, candidate, "one");
        Assert.Equal(candidate.Commit, service.Register(worker.Path).Commit);
        Assert.Equal(start, service.Register(plan.Path));
        Assert.Equal(merged.Commit, service.PrepareMerge(plan, start.Commit,
            candidate, "one").Commit);
        var advanced = service.AdvancePlan(merged);
        Assert.Equal(merged.Commit, advanced.Commit);
        Assert.Equal(advanced, service.AdvancePlan(merged));
        Assert.Equal(original, service.Register(Source));
        Assert.False(File.Exists(Path.Combine(Source, "code.txt")));
    }

    [Fact]
    public void RejectsOutOfScopeRenameAndUntrackedFilesBeforeCapture()
    {
        var start = service.Register(Source);
        var worker = service.PlanAttempt(start, Worktrees, "task", "bad");
        service.CreateAttempt(worker);
        var target = Path.Combine(worker.Path, "owned");
        File.Move(Path.Combine(worker.Path, "original ç.txt"), target);
        Assert.Throws<GitRepositoryException>(() =>
            service.CaptureCandidate(worker, null, path => path == "owned"));
        Assert.Equal(start.Commit, service.Inspect(worker.Path).Commit);
        File.WriteAllText(Path.Combine(worker.Path, "secret"), "private");
        Assert.Throws<GitRepositoryException>(() =>
            service.CaptureCandidate(worker, null, path => path != "secret"));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void KeepsBothConflictingBranchesAndPreservesThePlan()
    {
        var original = service.Register(Source);
        var plan = service.PlanWorkspace(original, Worktrees, "conflicts");
        var basis = service.EnsureWorkspace(plan, original.Commit);
        var first = EditCandidate(basis, "one", "first");
        var second = EditCandidate(basis, "two", "second");
        var merged = service.PrepareMerge(plan, basis.Commit, first, "one");
        // Simulate termination after staging and before the ref transaction.
        Git(plan.Path, "read-tree", "-u", "-m", basis.Commit, merged.Commit);
        var advanced = new GitRepositoryService().AdvancePlan(merged);
        var error = Assert.Throws<GitRepositoryException>(() =>
            service.PrepareMerge(plan, advanced.Commit, second, "two"));
        Assert.Equal(advanced, service.Register(plan.Path));
        var conflict = service.ListWorktrees(original.Repository)
            .Single(w => w.Branch?.StartsWith("pda/merge/") == true &&
                w.Branch != merged.Branch);
        Assert.Contains(conflict.Path, error.Message);
        Assert.False(service.Inspect(conflict.Path).IsClean);
        Assert.Equal(original, service.Register(Source));
    }

    private GitCandidate EditCandidate(RepositoryBaseline basis, string id,
        string contents)
    {
        var worker = service.PlanAttempt(basis, Worktrees, "task", id);
        service.CreateAttempt(worker);
        var path = Path.Combine(worker.Path, "original ç.txt");
        File.WriteAllText(path, contents);
        return service.CaptureCandidate(worker, null, _ => true);
    }
    [Fact]
    public void RefusesChangedMergeAndDirtyPlanWithoutAdvancing()
    {
        var original = service.Register(Source);
        var plan = service.PlanWorkspace(original, Worktrees, "plan");
        var start = service.EnsureWorkspace(plan, original.Commit);
        var worker = service.PlanAttempt(start, Worktrees, "task", "first");
        service.CreateAttempt(worker);
        File.WriteAllText(Path.Combine(worker.Path, "file"), "candidate");
        var candidate = service.CaptureCandidate(worker, null, _ => true);
        var result = service.PrepareMerge(plan, start.Commit, candidate, "x");
        File.WriteAllText(Path.Combine(plan.Path, "untracked"), "preserve");
        Assert.Throws<GitRepositoryException>(() =>
            service.AdvancePlan(result));
        Assert.Equal(start.Commit, service.Inspect(plan.Path).Commit);
        Assert.Equal("preserve", File.ReadAllText(
            Path.Combine(plan.Path, "untracked")));
        File.Delete(Path.Combine(plan.Path, "untracked"));
        Git(result.Path, "commit", "--allow-empty", "-m", "changed");
        Assert.Throws<GitRepositoryException>(() =>
            service.AdvancePlan(result));
        Assert.Equal(start, service.Register(plan.Path));
    }
}
