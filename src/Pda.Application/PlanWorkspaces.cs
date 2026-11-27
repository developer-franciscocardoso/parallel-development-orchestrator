using Pda.Core;
using Pda.Planning;
using Pda.Repositories;

namespace Pda.Application;

public sealed record IntegrationHead(string Commit);

public sealed class PlanWorkspaces(IStateStore store, string worktreeRoot)
{
    private readonly GitRepositoryService git = new();

    // The coordinator holds the startup guard across intent and creation.
    public RepositoryBaseline Open(PlanInfo plan)
    {
        var profile = store.ProjectProfiles<ProjectProfile>(plan.ProjectId)
            .SingleOrDefault(p => p.Version == plan.ProfileVersion)?.Data;
        if (profile == null || profile.Commit != plan.BaselineCommit)
            throw new InvalidOperationException("Missing pinned plan profile.");
        var record = store.Artifacts<PlanWorktree>(plan.Id, "workspace")
            .SingleOrDefault(a => a.Id == plan.Id);
        var tree = record?.Data ?? Prepare(plan, profile);
        if (tree.PlanId != plan.Id || tree.Baseline.Commit != profile.Commit ||
            tree.Baseline.Repository.CommonDirectory != profile.GitCommonDirectory)
            throw new InvalidDataException("Plan workspace identity mismatch.");
        var head = store.Artifacts<IntegrationHead>(plan.Id, "integration-head")
            .SingleOrDefault(a => a.Id == plan.Id)?.Data.Commit;
        return git.EnsureWorkspace(tree, head ?? profile.Commit);
    }

    private PlanWorktree Prepare(PlanInfo plan, ProjectProfile profile)
    {
        var baseline = git.Register(profile.Root);
        if (baseline.Commit != profile.Commit ||
            baseline.Repository.CommonDirectory != profile.GitCommonDirectory)
            throw new InvalidOperationException("Registered baseline has moved.");
        var tree = git.PlanWorkspace(baseline, worktreeRoot, plan.Id);
        store.PutArtifact(plan.Id, "workspace", plan.Id, tree);
        return tree;
    }
}
