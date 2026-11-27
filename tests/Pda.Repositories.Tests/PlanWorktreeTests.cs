using Pda.Repositories;

namespace Pda.Repositories.Tests;

public sealed partial class GitRepositoryTests
{
    [Fact]
    public void AdoptsCleanPlanWorktreeAcrossRestartAndPreservesUserChanges()
    {
        var source = service.Register(Source);
        var plan = service.PlanWorkspace(source, Worktrees, "Q4.plan-1");
        var workspace = service.EnsureWorkspace(plan, source.Commit);
        Assert.Equal(plan.Path, workspace.Repository.Root);
        Assert.NotEqual(source.Branch, workspace.Branch);
        File.WriteAllText(Path.Combine(Source, "notes"), "local changes");

        var reopened = new GitRepositoryService()
            .EnsureWorkspace(plan, source.Commit);
        Assert.Equal(workspace, reopened);
        Assert.Equal("local changes", File.ReadAllText(
            Path.Combine(Source, "notes")));
        Assert.Equal(source.Branch, service.Inspect(Source).Branch);
    }

    [Fact]
    public void RequiresExpectedPlanHeadAndPreservesDirtyPlanEdits()
    {
        var source = service.Register(Source);
        var plan = service.PlanWorkspace(source, Worktrees, "p");
        service.EnsureWorkspace(plan, source.Commit);
        var file = Path.Combine(plan.Path, "candidate");
        File.WriteAllText(file, "changes");
        Assert.Throws<GitRepositoryException>(() =>
            service.EnsureWorkspace(plan, source.Commit));
        Git(plan.Path, "add", "--", "candidate");
        Git(plan.Path, "commit", "-m", "advance");
        var advanced = service.Register(plan.Path);
        Assert.Throws<GitRepositoryException>(() =>
            service.EnsureWorkspace(plan, source.Commit));
        Assert.Equal(advanced, service.EnsureWorkspace(plan, advanced.Commit));
        Assert.Equal(source, service.Register(Source));
    }

    [Fact]
    public void RefusesForeignCheckoutObstructionsAndForgedPaths()
    {
        var source = service.Register(Source);
        var plan = service.PlanWorkspace(source, Worktrees, "p");
        Assert.Throws<GitRepositoryException>(() => service.EnsureWorkspace(
            plan with { Path = Source }, source.Commit));
        Assert.Throws<GitRepositoryException>(() => service.EnsureWorkspace(
            plan with { Branch = source.Branch }, source.Commit));
        Git(Source, "clone", "--no-local", "--", Source, plan.Path);
        Assert.Throws<GitRepositoryException>(() =>
            service.EnsureWorkspace(plan, source.Commit));
        Assert.True(Directory.Exists(Path.Combine(plan.Path, ".git")));
    }

    [Fact]
    public void RequiresCleanPinnedSourceOnFirstCreation()
    {
        var source = service.Register(Source);
        var plan = service.PlanWorkspace(source, Worktrees, "p");
        Git(Source, "commit", "--allow-empty", "-m", "advanced source");
        Assert.Throws<GitRepositoryException>(() =>
            service.EnsureWorkspace(plan, source.Commit));
        Assert.False(Directory.Exists(plan.Path));
    }
}
